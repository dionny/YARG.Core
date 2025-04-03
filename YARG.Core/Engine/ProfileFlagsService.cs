// YARG.Core/Engine/ProfileFlagsService.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using YARG.Core.Logging;

namespace YARG.Core.Engine
{
    /// <summary>
    /// Manages boolean gameplay flags per profile and hosts an HTTP API for control.
    /// This should be treated as a singleton.
    /// </summary>
    public class ProfileFlagsService : IDisposable
    {
        public static ProfileFlagsService Instance { get; private set; } = new();

        private const string LISTENER_PREFIX = "http://localhost:7007/flags/"; // Renamed endpoint base
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _serverTokenSource = new();
        private Task? _serverTask;

        // Default states for flags when a profile is first encountered or reset
        private readonly Dictionary<ProfileFlag, bool> _defaultFlagStates = new()
        {
            { ProfileFlag.AutoStrum, false },
            // Add defaults for future flags here
        };

        // Stores the explicit state of flags for each profile ID.
        // Outer dictionary maps ProfileId -> Inner dictionary
        // Inner dictionary maps ProfileFlag -> bool (enabled/disabled)
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<ProfileFlag, bool>> _profileFlagStates = new();

        // Tracks profiles currently active in the game session for the /status endpoint
        private readonly ConcurrentDictionary<Guid, byte> _knownProfiles = new(); // Value (byte) is unused

        private ProfileFlagsService() { } // Private constructor for singleton pattern

        public void StartServer()
        {
            if (_listener.IsListening)
            {
                YargLogger.LogWarning("ProfileFlagsService server is already running.");
                return;
            }

            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add(LISTENER_PREFIX);
                _listener.Start();
                YargLogger.LogInfo($"Profile Flags API server started on {LISTENER_PREFIX}");
                _serverTask = Task.Run(() => HandleRequestsAsync(_serverTokenSource.Token));
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex, $"Failed to start Profile Flags API server on {LISTENER_PREFIX}");
                StopServer();
            }
        }

        public void StopServer()
        {
            if (!_listener.IsListening) return;
            YargLogger.LogInfo("Stopping Profile Flags API server...");
            try
            {
                _serverTokenSource.Cancel();
                _listener.Stop();
                _listener.Close();
                _serverTask?.Wait(TimeSpan.FromSeconds(1));
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex, "Error while stopping Profile Flags API server.");
            }
            finally
            {
                _serverTask = null;
                YargLogger.LogInfo("Profile Flags API server stopped.");
            }
        }

        // --- Profile Management (Called by GameManager) ---

        public void RegisterProfile(Guid profileId)
        {
            _knownProfiles.TryAdd(profileId, 0);
            YargLogger.LogTrace($"Profile registered with FlagsService: {profileId}");
        }

        public void RegisterProfiles(IEnumerable<Guid> profileIds)
        {
            foreach(var id in profileIds)
            {
                 _knownProfiles.TryAdd(id, 0);
            }
             YargLogger.LogTrace($"Registered {profileIds.Count()} profiles with FlagsService.");
        }

        public void DeregisterProfile(Guid profileId)
        {
            _knownProfiles.TryRemove(profileId, out _);
            // Optionally remove explicit states too, or let them persist
            // _profileFlagStates.TryRemove(profileId, out _);
            YargLogger.LogTrace($"Profile deregistered from FlagsService: {profileId}");
        }

        public void ClearRegisteredProfiles()
        {
            _knownProfiles.Clear();
             YargLogger.LogTrace($"Cleared all registered profiles from FlagsService.");
        }

        // --- Flag State Management ---

        /// <summary>
        /// Checks if a specific flag is enabled for a given profile ID.
        /// Returns the flag's default value if it hasn't been explicitly set for the profile.
        /// </summary>
        public bool IsFlagSet(Guid profileId, ProfileFlag flag)
        {
            // If profile has explicit states, check for the flag
            if (_profileFlagStates.TryGetValue(profileId, out var flags))
            {
                if (flags.TryGetValue(flag, out bool isEnabled))
                {
                    return isEnabled;
                }
            }

            // Otherwise, return the default state for that flag
            return _defaultFlagStates.TryGetValue(flag, out bool defaultState) && defaultState;
        }

        /// <summary>
        /// Sets the state of a specific flag for a specific profile ID.
        /// </summary>
        public void SetFlagState(Guid profileId, ProfileFlag flag, bool enabled)
        {
            // Ensure the outer dictionary has an entry for this profile
            var flags = _profileFlagStates.GetOrAdd(profileId, _ => new ConcurrentDictionary<ProfileFlag, bool>());

            // Set the specific flag state
            flags[flag] = enabled;

            // Also ensure the profile is marked as "known" if set via API
            RegisterProfile(profileId);

            YargLogger.LogInfo($"Flag '{flag}' for profile {profileId} set to {enabled}");
        }

        // --- HTTP Request Handling ---

        private async Task HandleRequestsAsync(CancellationToken cancellationToken)
        {
            while (_listener.IsListening && !cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext? context = null;
                try
                {
                    context = await _listener.GetContextAsync();
                    await ProcessRequestContextAsync(context);
                }
                catch (HttpListenerException hle) when (hle.ErrorCode == 995) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    YargLogger.LogException(ex, "Error handling Profile Flags API request.");
                    if (context?.Response != null && !context.Response.OutputStream.CanWrite)
                    {
                        try { context.Response.StatusCode = (int)HttpStatusCode.InternalServerError; context.Response.OutputStream.Close(); } catch {}
                    }
                }
            }
        }

        private async Task ProcessRequestContextAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            string responseString = "";
            response.ContentType = "application/json";
            response.ContentEncoding = Encoding.UTF8;

            string? path = request.Url?.AbsolutePath.ToLowerInvariant().TrimEnd('/');
            string method = request.HttpMethod.ToUpperInvariant();

            YargLogger.LogTrace($"Profile Flags API Request: {method} {path}");

            try
            {
                if (path == null || !path.StartsWith("/flags"))
                {
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    responseString = "{\"error\": \"Invalid path base. Use /flags/\"}";
                }
                else
                {
                    string remainingPath = path.Substring("/flags".Length);

                    if (method == "GET" && remainingPath == "/status")
                    {
                        // GET /flags/status - View current states for all known profiles
                        var statusDict = new Dictionary<Guid, Dictionary<string, bool>>();
                        var allFlags = (ProfileFlag[])Enum.GetValues(typeof(ProfileFlag));

                        foreach (var profileId in _knownProfiles.Keys)
                        {
                            var profileStatus = new Dictionary<string, bool>();
                            foreach (var flag in allFlags)
                            {
                                // Skip "None" flag
                                if (flag == ProfileFlag.None) continue;
                                profileStatus[flag.ToString()] = IsFlagSet(profileId, flag);
                            }
                            statusDict[profileId] = profileStatus;
                        }

                        responseString = JsonConvert.SerializeObject(statusDict, Formatting.Indented);
                        response.StatusCode = (int)HttpStatusCode.OK;
                    }
                    else if (method == "PUT" && remainingPath.StartsWith("/set/"))
                    {
                        // PUT /flags/set/{profileId}/{flagName}/{state} - Set flag state
                        string[] parts = remainingPath.Substring("/set/".Length).Split('/');
                        if (parts.Length == 3 &&
                            Guid.TryParse(parts[0], out Guid profileId) &&
                            Enum.TryParse<ProfileFlag>(parts[1], true, out ProfileFlag flag) && // Case-insensitive flag name parsing
                            bool.TryParse(parts[2], out bool state))
                        {
                            if (flag != ProfileFlag.None) // Don't allow setting "None"
                            {
                                SetFlagState(profileId, flag, state);
                                responseString = $"{{\"profileId\": \"{profileId}\", \"flag\": \"{flag}\", \"enabled\": {state.ToString().ToLowerInvariant()}}}";
                                response.StatusCode = (int)HttpStatusCode.OK;
                            }
                            else
                            {
                                response.StatusCode = (int)HttpStatusCode.BadRequest;
                                responseString = $"{{\"error\": \"Cannot set the 'None' flag.\"}}";
                            }
                        }
                        else
                        {
                            response.StatusCode = (int)HttpStatusCode.BadRequest;
                            responseString = $"{{\"error\": \"Invalid request format. Use PUT /flags/set/{{profileId}}/{{flagName}}/{{true|false}}\"}}";
                            if (parts.Length >= 2 && !Enum.TryParse<ProfileFlag>(parts[1], true, out _))
                            {
                                 responseString = $"{{\"error\": \"Invalid flag name: {parts[1]}\"}}";
                            }
                             else if (parts.Length >= 1 && !Guid.TryParse(parts[0], out _))
                            {
                                 responseString = $"{{\"error\": \"Invalid Profile ID format: {parts[0]}\"}}";
                            }
                        }
                    }
                    // Deprecated enable/disable endpoints - redirecting logic to /set
                    else if (method == "PUT" && remainingPath.StartsWith("/enable/"))
                    {
                        string[] parts = remainingPath.Substring("/enable/".Length).Split('/');
                         if (parts.Length == 2 &&
                            Guid.TryParse(parts[0], out Guid profileId) &&
                            Enum.TryParse<ProfileFlag>(parts[1], true, out ProfileFlag flag))
                        {
                             if (flag != ProfileFlag.None)
                             {
                                SetFlagState(profileId, flag, true);
                                responseString = $"{{\"profileId\": \"{profileId}\", \"flag\": \"{flag}\", \"enabled\": true}}";
                                response.StatusCode = (int)HttpStatusCode.OK;
                             }
                             else
                             {
                                response.StatusCode = (int)HttpStatusCode.BadRequest;
                                responseString = $"{{\"error\": \"Cannot set the 'None' flag.\"}}";
                             }
                        }
                        else
                        {
                            response.StatusCode = (int)HttpStatusCode.BadRequest;
                            responseString = $"{{\"error\": \"Invalid request format. Use PUT /flags/enable/{{profileId}}/{{flagName}}\"}}";
                            if (parts.Length >= 2 && !Enum.TryParse<ProfileFlag>(parts[1], true, out _)) responseString = $"{{\"error\": \"Invalid flag name: {parts[1]}\"}}";
                            else if (parts.Length >= 1 && !Guid.TryParse(parts[0], out _)) responseString = $"{{\"error\": \"Invalid Profile ID format: {parts[0]}\"}}";
                        }
                    }
                    else if (method == "PUT" && remainingPath.StartsWith("/disable/"))
                    {
                         string[] parts = remainingPath.Substring("/disable/".Length).Split('/');
                         if (parts.Length == 2 &&
                            Guid.TryParse(parts[0], out Guid profileId) &&
                            Enum.TryParse<ProfileFlag>(parts[1], true, out ProfileFlag flag))
                        {
                              if (flag != ProfileFlag.None)
                             {
                                SetFlagState(profileId, flag, false);
                                responseString = $"{{\"profileId\": \"{profileId}\", \"flag\": \"{flag}\", \"enabled\": false}}";
                                response.StatusCode = (int)HttpStatusCode.OK;
                             }
                             else
                             {
                                response.StatusCode = (int)HttpStatusCode.BadRequest;
                                responseString = $"{{\"error\": \"Cannot set the 'None' flag.\"}}";
                             }
                        }
                        else
                        {
                            response.StatusCode = (int)HttpStatusCode.BadRequest;
                            responseString = $"{{\"error\": \"Invalid request format. Use PUT /flags/disable/{{profileId}}/{{flagName}}\"}}";
                             if (parts.Length >= 2 && !Enum.TryParse<ProfileFlag>(parts[1], true, out _)) responseString = $"{{\"error\": \"Invalid flag name: {parts[1]}\"}}";
                            else if (parts.Length >= 1 && !Guid.TryParse(parts[0], out _)) responseString = $"{{\"error\": \"Invalid Profile ID format: {parts[0]}\"}}";
                        }
                    }
                    else
                    {
                        response.StatusCode = (int)HttpStatusCode.NotFound;
                        responseString = $"{{\"error\": \"Endpoint not found or method not allowed: {method} {remainingPath}\"}}";
                    }
                }
            }
            catch (Exception ex)
            {
                 YargLogger.LogException(ex, $"Internal error processing API request {method} {path}");
                 response.StatusCode = (int) HttpStatusCode.InternalServerError;
                 responseString = $"{{\"error\": \"Internal server error: {ex.Message}\"}}";
            }

            byte[] buffer = Encoding.UTF8.GetBytes(responseString);
            response.ContentLength64 = buffer.Length;
            try
            {
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
            catch (Exception writeEx)
            {
                 YargLogger.LogException(writeEx, $"Error writing API response for {method} {path}");
            }
            finally
            {
                response.OutputStream.Close();
            }
        }

        public void Dispose()
        {
            StopServer();
            _listener.Close();
            _serverTokenSource.Dispose();
        }
    }
}