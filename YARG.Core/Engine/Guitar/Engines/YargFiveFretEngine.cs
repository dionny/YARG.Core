using System;
using YARG.Core.Chart;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Core.Engine; // Required for ProfileFlag, ProfileFlagsService, BaseEngine access
using System.Numerics;  // Required for ProfileFlagsService and ProfileFlag if used there

namespace YARG.Core.Engine.Guitar.Engines
{
    /// <summary>
    /// The standard 5-fret guitar engine used by YARG.
    /// Handles note hitting, sustains, HOPOs, taps, Star Power, and multipliers.
    /// Includes modifications for AutoStrum and AutoPlay profile flags.
    /// </summary>
    public class YargFiveFretEngine : GuitarEngine
    {
        // Constructor remains the same as provided
        public YargFiveFretEngine(InstrumentDifficulty<GuitarNote> chart, SyncTrack syncTrack,
            GuitarEngineParameters engineParameters, bool isBot)
            : base(chart, syncTrack, engineParameters, isBot)
        {
            YargLogger.LogTrace($"YargFiveFretEngine created. Profile ID must be set externally for ProfileFlagsService control.");
            // Base class constructor handles initialization of timers etc.
        }

        // IsAutoPlayActive() and IsAutoStrumActive() are assumed to be implemented in BaseEngine

        /// <summary>
        /// Updates the engine state based on the bot's decisions. Disabled during AutoPlay.
        /// </summary>
        protected override void UpdateBot(double time)
        {
            // AutoPlay overrides bot logic entirely
            if (IsAutoPlayActive())
            {
                return;
            }

            // Original Bot logic
            if (!IsBot || NoteIndex >= Notes.Count) return;

            var note = Notes[NoteIndex];
            // Bot acts exactly on the note's time
            if (time < note.Time) return;

            // Simulate perfect fret holding for the current note
            LastButtonMask = ButtonMask;
            ButtonMask = (byte) note.NoteMask;

            // Bot logic needs to determine if it's a tap/hopo situation vs strum
            // Simplified: Assume bot simulates perfect input for whatever is needed.
            // Set HasTapped if frets changed, IsFretPress simulates holding down
            HasTapped = ButtonMask != LastButtonMask;
            IsFretPress = true;
            // Bot doesn't "strum" manually; HasStrummed false, relies on AutoStrum logic if enabled,
            // or direct hit logic if AutoPlay/Bot hits strums.
            HasStrummed = false; // Let CheckForNoteHit handle AutoStrum for bot if needed

            // Ensure sustains are held by the bot's ButtonMask
            // (Note: CanSustainHold logic in GuitarEngine/BaseEngine should ideally handle this)
            foreach (var sustain in ActiveSustains)
            {
                var sustainNote = sustain.Note;
                if (!sustainNote.IsExtendedSustain) continue;
                // Check if bot *should* be holding this sustain based on game rules
                if (!CanSustainHold(sustainNote)) continue;

                // Add the sustain's required frets to the bot's mask
                if (sustainNote.IsDisjoint) ButtonMask |= (byte) sustainNote.DisjointMask;
                else ButtonMask |= (byte) sustainNote.NoteMask;
            }

            // Handle Open note for bot
            if ((ButtonMask & ~OPEN_MASK) == 0) ButtonMask |= OPEN_MASK;
            else ButtonMask &= unchecked((byte)~OPEN_MASK);
        }

        /// <summary>
        /// Updates the engine's internal state based on player input.
        /// During AutoPlay, fret and strum inputs are ignored for state mutation,
        /// but Star Power and Whammy are still processed.
        /// </summary>
        protected override void MutateStateWithInput(GameInput gameInput)
        {
            bool isAutoPlay = IsAutoPlayActive();
            var action = gameInput.GetAction<GuitarAction>();

            // --- Always process SP and Whammy ---
            if (action is GuitarAction.StarPower)
            {
                IsStarPowerInputActive = gameInput.Button;
            }
            else if (action is GuitarAction.Whammy)
            {
                if (gameInput.Button) StarPowerWhammyTimer.Start(gameInput.Time);
            }

            // --- Process Fret Inputs ---
            if (IsFretInput(gameInput))
            {
                // Store previous mask *before* changing ButtonMask. Might be needed for visuals elsewhere.
                // Only store if AutoPlay is OFF, as LastButtonMask is used for ghosting checks disabled in AutoPlay.
                if (!isAutoPlay) {
                    LastButtonMask = ButtonMask;
                }

                // ALWAYS update the ButtonMask for visual feedback via ToggleFret in base class
                ToggleFret(gameInput.Action, gameInput.Button);

                // ALWAYS update the Open Note state based on the possibly changed ButtonMask
                if ((ButtonMask & ~OPEN_MASK) == 0) ButtonMask |= OPEN_MASK;
                else ButtonMask &= unchecked((byte)~OPEN_MASK);

                // --- Conditionally update Hit Logic Flags ---
                // ONLY set flags used for hit logic if AutoPlay is OFF
                if (!isAutoPlay)
                {
                    HasFretted = true;
                    IsFretPress = gameInput.Button;
                    // LastButtonMask update moved above condition, re-evaluate if needed
                }
            }
            // --- Process Strum Inputs ---
            else if (action is GuitarAction.StrumDown or GuitarAction.StrumUp && gameInput.Button)
            {
                // --- Conditionally update Hit Logic Flag ---
                // ONLY set HasStrummed if AutoPlay is OFF and AutoStrum is also OFF
                bool isAutoStrum = IsAutoStrumActive();
                if (!isAutoPlay && !isAutoStrum)
                {
                    HasStrummed = true;
                }
            }

            // Note: We removed the early 'return;' for isAutoPlay.
            // The critical part is that CanNoteBeHit, CheckForNoteHit's AutoPlay path,
            // and CanSustainHold all correctly bypass using the ButtonMask when AutoPlay is active.
        }

        /// <summary>
        /// Main update loop for hit logic, called multiple times per frame if necessary.
        /// Handles state updates based on time, timers, AutoPlay, AutoStrum, and player actions.
        /// </summary>
        protected override void UpdateHitLogic(double time)
        {
            bool isAutoPlay = IsAutoPlayActive();
            bool isAutoStrum = IsAutoStrumActive(); // Check AutoStrum status (relevant if not AutoPlay)

            UpdateStarPower(); // Update Star Power state (applies even in AutoPlay)
            UpdateTimers();    // Update engine timers (leniency etc.)

            // --- AutoPlay State Management ---
            if (isAutoPlay)
            {
                // Reset player-specific action flags. Autoplay dictates actions.
                HasStrummed = false;
                HasFretted = false;
                HasTapped = false; // Autoplay handles taps/hopos directly
                IsFretPress = false;
                WasNoteGhosted = false; // No ghosting in autoplay
                // ButtonMask is NOT reset here; it reflects ignored player input for visuals/audio.
                // Autoplay hit logic bypasses ButtonMask checks.

                // Autoplay logic runs within CheckForNoteHit
            }
            // --- Manual / AutoStrum State Management ---
            else // Only run this block if AutoPlay is OFF
            {
                // Process Manual Strum input (only if AutoStrum is OFF)
                if (!isAutoStrum && HasStrummed)
                {
                    bool strumEatenByHopo = false;
                    // Check if strum occurs during HOPO leniency window
                    if (HopoLeniencyTimer.IsActive)
                    {
                        StrumLeniencyTimer.Disable(); // Cancel any pending strum leniency
                        HopoLeniencyTimer.Disable();  // Consume HOPO leniency (hit the HOPO)
                        strumEatenByHopo = true;
                        ReRunHitLogic = true; // Re-evaluate immediately to process the HOPO hit
                        YargLogger.LogFormatTrace("Strum eaten by HOPO leniency at {0}", CurrentTime);
                    }
                    // Check if strumming again while strum leniency is already active (Overstrum)
                    else if (StrumLeniencyTimer.IsActive)
                    {
                        Overstrum(); // Trigger Overstrum penalty (checks internally for AutoPlay)
                        StrumLeniencyTimer.Disable(); // Overstrum consumes the timer
                        ReRunHitLogic = true; // State changed (combo reset, etc.)
                    }

                    // If strum wasn't eaten and didn't cause overstrum, start strum leniency
                    if (!strumEatenByHopo && !StrumLeniencyTimer.IsActive) // Check !IsActive again
                    {
                        // Calculate offset for timer (prevents starting if already past note)
                        double offset = 0;
                        if (NoteIndex < Notes.Count)
                        {
                            // Use small offset only if completely past the note's backend window
                            double backend = Notes[NoteIndex].Time + EngineParameters.HitWindow.GetBackEnd(EngineParameters.HitWindow.CalculateHitWindow(GetAverageNoteDistance(Notes[NoteIndex])));
                            if (CurrentTime > backend) offset = EngineParameters.StrumLeniencySmall;
                        } else {
                             // If no notes left, use small offset
                            offset = EngineParameters.StrumLeniencySmall;
                        }

                        StartTimer(ref StrumLeniencyTimer, CurrentTime, offset);
                        YargLogger.LogFormatTrace("Strum Leniency timer started at {0} with offset {1}, ends at {2}", CurrentTime, offset, StrumLeniencyTimer.EndTime);
                        ReRunHitLogic = true; // Timer state changed, needs re-evaluation
                    }
                } // End manual strum processing

                // Update Bot (only if IsBot is true and AutoPlay is false)
                UpdateBot(time);

                // Handle Ghost Input Detection (only if not AutoPlay and fretting occurred)
                if (HasFretted && EngineParameters.AntiGhosting)
                {
                    // Check if the current note is potentially hittable to evaluate ghosting
                    if (NoteIndex < Notes.Count)
                    {
                        var note = Notes[NoteIndex];
                         // Check if note is roughly in the upcoming window or just passed
                         double backend = note.Time + EngineParameters.HitWindow.GetBackEnd(EngineParameters.HitWindow.CalculateHitWindow(GetAverageNoteDistance(note)));
                         if (CurrentTime <= backend) // Only check ghosting if relevant to current/next note
                         {
                             // Set HasTapped = true when fretting occurs, enabling HOPO/Tap logic check
                             HasTapped = true;
                             // Set FrontEndExpireTime for anti-ghosting window
                             var hitWindow = EngineParameters.HitWindow.CalculateHitWindow(GetAverageNoteDistance(note));
                             var frontEnd = EngineParameters.HitWindow.GetFrontEnd(hitWindow);
                             FrontEndExpireTime = CurrentTime + Math.Abs(frontEnd); // Use absolute for safety

                             // Check for ghost input if the action was a fret press
                             if (IsFretPress)
                             {
                                bool ghosted = CheckForGhostInput(note);
                                // Persist ghosted state until next successful hit or miss
                                WasNoteGhosted = ghosted || WasNoteGhosted;
                                if (ghosted) EngineStats.GhostInputs++;
                             }
                         }
                    }
                } // End ghost input handling
            } // End Manual/AutoStrum block

            // --- Hit Checking (Handles AutoPlay internally) ---
            CheckForNoteHit();

            // --- Update Sustains (Handles AutoPlay internally via CanSustainHold) ---
            UpdateSustains(); // Assumes UpdateSustains in BaseEngine handles AutoPlay

            // --- Reset Per-Frame Player Action Flags (Only if not AutoPlay) ---
            if (!isAutoPlay)
            {
                HasStrummed = false; // Reset for next frame's input processing
                HasFretted = false;  // Reset for next frame's input processing
                IsFretPress = false; // Reset for next frame's input processing
                // HasTapped is managed by hit/miss/fret logic
                // WasNoteGhosted is managed by hit/miss/ghost logic
            }
        }

        /// <summary>
        /// Checks notes against the current time and engine state to determine hits or misses.
        /// Contains separate logic paths for AutoPlay and Manual/AutoStrum modes.
        /// </summary>
        protected override void CheckForNoteHit()
        {
            bool isAutoPlay = IsAutoPlayActive();
            bool isAutoStrum = IsAutoStrumActive(); // Relevant only if not AutoPlay

            for (int i = NoteIndex; i < Notes.Count; i++)
            {
                bool isFirstNoteInWindow = i == NoteIndex;
                var note = Notes[i];

                // 1. Skip notes already Hit or Missed
                if (note.WasFullyHitOrMissed())
                {
                    // NoteIndex advancement is handled within HitNote/MissNote calls now
                    continue;
                }

                // 2. Check if the note is within the timing window
                if (!IsNoteInWindow(note, out bool missed))
                {
                    // Note is outside the window
                    if (isFirstNoteInWindow && missed)
                    {
                        // Current note is past the backend: Miss it
                        YargLogger.LogFormatTrace("Missing note {0} (time: {1}) due to being past back-end window at {2}", i, note.Time, CurrentTime);
                        MissNote(note); // MissNote handles state changes, NoteIndex++, ReRunHitLogic
                        // Since the primary note was missed, break the loop for this frame update
                        break;
                    }
                    else if (isFirstNoteInWindow && !missed)
                    {
                        // Current note is before the front-end: Stop checking notes for this frame
                        YargLogger.LogFormatTrace("Note {0} (time: {1}) not yet in front-end window at {2}", i, note.Time, CurrentTime);
                        break;
                    }
                    else
                    {
                        // A later note (not i == NoteIndex) is outside the window.
                        // Continue checking other notes in case the primary note was hit this frame.
                        continue;
                    }
                }

                // Note is within the timing window (frontend <= time <= backend)

                // --- AUTOPLAY HIT LOGIC ---
                if (isAutoPlay)
                {
                    // In AutoPlay, if a note is in the window and not already dealt with, HIT IT.
                    // Fret checks, strum/tap conditions are irrelevant.
                    YargLogger.LogFormatTrace("Attempting AutoPlay hit for note {0} (time: {1}) at {2}", i, note.Time, CurrentTime);
                    HitNote(note); // HitNote handles score, combo, sustain start, NoteIndex++, ReRunHitLogic
                    // Process only one hit per engine update cycle in AutoPlay to avoid potential issues
                    break;
                }
                // --- MANUAL / AUTOSTRUM HIT LOGIC ---
                else
                {
                    // 3. Check if frets are held correctly (if not AutoPlay)
                    if (!CanNoteBeHit(note))
                    {
                        // Frets are wrong.
                        // Log if manual strum occurred with wrong frets for the current target note
                        if (!isAutoStrum && isFirstNoteInWindow && HasStrummed && StrumLeniencyTimer.IsActive)
                        {
                            YargLogger.LogFormatTrace("Strum Leniency active but frets wrong for note {0} at {1}", i, CurrentTime);
                            // Don't overstrum yet, let timer expire if frets aren't corrected.
                        }

                        // If it's the primary note, we can't hit it or any subsequent notes this frame.
                        if (isFirstNoteInWindow)
                        {
                             YargLogger.LogFormatTrace("Note {0} in window, but cannot be hit (wrong frets) at {1}", i, CurrentTime);
                             break;
                        }
                        // If not the primary note, maybe a later note can be hit (e.g., held chord allows hitting next note).
                        continue;
                    }

                    // Frets are correct. Now check input conditions.

                    // 4. Check HOPO/Tap Hit Condition
                    bool hopoCondition = note.IsHopo && (EngineStats.Combo > 0 || NoteIndex == 0); // HOPO needs combo or first note
                    bool tapCondition = note.IsTap; // Taps generally hit if frets match
                    bool frontEndIsExpired = FrontEndExpireTime > 0 && CurrentTime > FrontEndExpireTime; // Check anti-ghost timer
                    bool canUseInfFrontEnd = EngineParameters.InfiniteFrontEnd || !frontEndIsExpired || NoteIndex == 0 || FrontEndExpireTime == 0;

                    // Check if tapped, conditions met, and not ghosted
                    if (HasTapped && (hopoCondition || tapCondition) && canUseInfFrontEnd && !WasNoteGhosted)
                    {
                        YargLogger.LogFormatTrace("Attempting HOPO/Tap hit for note {0} (time: {1}) at {2}", i, note.Time, CurrentTime);
                        HitNote(note); // HitNote handles state, NoteIndex++, ReRunHitLogic
                        break; // Process one hit per frame
                    }

                    // 5. Check Strum Hit Condition (Manual or AutoStrum)
                    bool manualStrumCondition = !isAutoStrum && (HasStrummed || StrumLeniencyTimer.IsActive);
                    bool autoStrumCondition = isAutoStrum; // AutoStrum acts as the trigger

                    // Strums (manual or auto) generally hit the *first* available note in the window that requires a strum,
                    // OR can hit HOPOs/Taps if the tap input failed or wasn't used.
                    // Check only applies to the primary note in the window (isFirstNoteInWindow)
                    if (isFirstNoteInWindow && (manualStrumCondition || autoStrumCondition))
                    {
                         // Check if the note is eligible for strumming (is strum note, or is HOPO/Tap being strummed)
                         // Note: Allow strumming HOPOs/Taps is standard behavior.
                        //  YargLogger.LogFormatTrace<Int32, Int32, Double, String>("Attempting Strum hit for note {0} (Mask: {1}, Type: {2}) at {3} with {4}",
                        //     i, note.NoteMask, note.IsHopo ? "HOPO" : (note.IsTap ? "Tap" : "Strum"), CurrentTime,
                        //     autoStrumCondition ? "AUTO-STRUM" : (HasStrummed ? "strum input" : "strum leniency"));

                        HitNote(note); // HitNote handles state, NoteIndex++, ReRunHitLogic
                        break; // Process one hit per frame
                    }

                    // 6. No Hit Condition Met for the Primary Note
                    // If it's the first note in the window, but frets were correct and no hit condition (tap/strum) was met,
                    // stop checking notes for this frame update cycle.
                    if (isFirstNoteInWindow)
                    {
                        YargLogger.LogFormatTrace("Note {0} in window, frets correct, but no hit condition met at {1}", i, CurrentTime);
                        break;
                    }

                } // End Manual/AutoStrum Hit Logic
            } // End note loop
        }

        /// <summary>
        /// Determines if the specified note can be hit with the current ButtonMask state.
        /// Returns true immediately if AutoPlay is active.
        /// Considers anchoring rules and sustains.
        /// </summary>
        protected override bool CanNoteBeHit(GuitarNote note)
        {
            // Bypass fret check entirely if AutoPlay is active
            if (IsAutoPlayActive())
            {
                return true;
            }

            // Use local variable for player's current input mask
            byte currentButtonMask = ButtonMask;

            // Factor in active sustains: Frets held for active *extended* sustains
            // might not count towards hitting the *next* note.
            byte buttonsMaskedForCheck = currentButtonMask;
            bool sustainMaskingApplied = false;
            foreach (var sustain in ActiveSustains)
            {
                var sustainNote = sustain.Note;
                // Only mask frets from *extended* sustains (regular sustains end on next note hit/miss)
                if (sustainNote.IsExtendedSustain && sustain.IsLeniencyHeld == false) // Check if actively held, not just in leniency
                {
                    var maskToRemove = sustainNote.IsDisjoint ? sustainNote.DisjointMask : sustainNote.NoteMask;
                    buttonsMaskedForCheck &= unchecked((byte)~maskToRemove);
                    sustainMaskingApplied = true;
                }
            }

            // Check if the note is hittable with the original mask OR the sustain-adjusted mask
            // This allows hitting notes even if higher frets are held for sustains.
            bool hittableWithOriginalMask = IsNoteHittableInternal(note, currentButtonMask);
            bool hittableWithSustainMask = sustainMaskingApplied && IsNoteHittableInternal(note, buttonsMaskedForCheck);

            return hittableWithOriginalMask || hittableWithSustainMask;


            // Internal helper for core hittability logic (based on YARG/GH rules)
            static bool IsNoteHittableInternal(GuitarNote note, byte buttonsHeld)
            {
                int noteMask = note.NoteMask;
                // If note is disjoint and already hit, use disjoint mask for sustain checks?
                // Let's stick to NoteMask for initial hit check for simplicity.
                // bool useDisjointSustainMask = note is { IsDisjoint: true, WasHit: true };
                // int noteMaskToCheck = useDisjointSustainMask ? note.DisjointMask : note.NoteMask;

                // Handle Open Notes specifically
                if (noteMask == OPEN_MASK)
                {
                    // To hit open, ONLY open should be active (no frets pressed)
                    return buttonsHeld == OPEN_MASK;
                }

                // Handle notes involving Open + Frets (e.g., Open G = Yellow + Open)
                bool noteRequiresOpen = (noteMask & OPEN_MASK) != 0;
                int noteFretsRequired = noteMask & ~OPEN_MASK;
                int buttonsFretsHeld = buttonsHeld & ~OPEN_MASK;

                // If note requires open, player must also have open state active (no frets held other than required)
                // Standard GH anchoring: allow higher frets held.
                if (noteRequiresOpen)
                {
                    // Must be holding the required frets
                    if ((buttonsFretsHeld & noteFretsRequired) != noteFretsRequired) return false;
                    // Must have the open state (meaning no non-required frets are held BELOW the lowest required fret)
                    int anchorFrets = buttonsFretsHeld ^ noteFretsRequired;
                    if (anchorFrets == 0) return true; // Exact fret match + open state implied = good
                    // Check if anchor frets are higher than the lowest required fret
                    int lowestRequiredFret = noteFretsRequired & -noteFretsRequired; // Isolate lowest set bit
                    return anchorFrets > lowestRequiredFret;
                }

                // --- Standard Fret Notes (No Open Required) ---
                // Required frets must be a subset of held frets
                if ((buttonsFretsHeld & noteFretsRequired) != noteFretsRequired)
                {
                    return false;
                }

                // Check anchoring: extra held frets (anchors) must be lower
                int anchorButtons = buttonsFretsHeld ^ noteFretsRequired;
                if (anchorButtons == 0)
                {
                    return true; // Exact match
                }

                // Find the lowest fret required by the note/chord
                int lowestRequiredFretMask = noteFretsRequired & -noteFretsRequired; // Isolate lowest required fret bit

                // Anchoring is valid only if all anchor buttons represent frets *lower* than the lowest required fret
                return anchorButtons < lowestRequiredFretMask;
            }
        }


        /// <summary>
        /// Called when a note is successfully hit. Manages timers, sustains, and calls base HitNote.
        /// Prevents player leniency timer starts during AutoPlay.
        /// </summary>
        protected override void HitNote(GuitarNote note)
        {
            bool isAutoPlay = IsAutoPlayActive();

            // Manage player-specific leniency timers ONLY if not in AutoPlay
            if (!isAutoPlay)
            {
                if (note.IsHopo || note.IsTap)
                {
                    HasTapped = false; // Consume the tap state that led to the hit
                    StartTimer(ref HopoLeniencyTimer, CurrentTime); // Grant HOPO leniency for next note
                }
                else // Strum hit
                {
                    // Resetting HasTapped on strum? Maybe not, tapping again might start HOPO chain.
                    EngineTimer.Reset(ref FrontEndExpireTime); // Reset anti-ghosting window timer
                }
                StrumLeniencyTimer.Disable(); // Successful hit consumes any active strum leniency
            }
            else // AutoPlay hit
            {
                // Ensure player leniency timers are disabled/reset when AutoPlay hits.
                // Prevents carry-over issues if AutoPlay is toggled off.
                HopoLeniencyTimer.Disable();
                StrumLeniencyTimer.Disable();
                EngineTimer.Reset(ref FrontEndExpireTime);
                HasTapped = false; // Reset tap state during AutoPlay
            }

            // Logic to end overlapping sustains (runs in both modes)
            // Iterate backwards for safe removal while iterating
            for (int i = ActiveSustains.Count - 1; i >= 0; i--)
            {
                var sustain = ActiveSustains[i];
                var sustainNote = sustain.Note;

                // Determine the masks to check for overlap (ignore open for overlap)
                var sustainMask = (sustainNote.IsDisjoint ? sustainNote.DisjointMask : sustainNote.NoteMask) & ~OPEN_MASK;
                var hitNoteMask = (note.IsDisjoint ? note.DisjointMask : note.NoteMask) & ~OPEN_MASK;

                // If the newly hit note shares any fret with the active sustain, end the sustain.
                if ((sustainMask & hitNoteMask) != 0)
                {
                    // End the sustain, mark as completed only if we reached its natural end time/tick
                    bool completed = CurrentTick >= sustainNote.TickEnd;
                    EndSustain(i, true, completed); // 'true' indicates it was ended due to note hit/overlap
                }
            }

            // Call base.HitNote for score, combo, basic state updates, event firing.
            // Base HitNote now calls AdvanceToNextNote internally.
            base.HitNote(note);
        }

        /// <summary>
        /// Called when a note is missed. Manages timers and calls base MissNote.
        /// Prevents player leniency timer interactions during AutoPlay.
        /// </summary>
        protected override void MissNote(GuitarNote note)
        {
            bool isAutoPlay = IsAutoPlayActive();

            // Reset player-specific state/timers ONLY if not in AutoPlay
            if (!isAutoPlay)
            {
                HasTapped = false; // Missing resets the tap state
                StrumLeniencyTimer.Disable(); // Missing consumes strum leniency (might lead to overstrum if timer was active)
                HopoLeniencyTimer.Disable(); // Missing breaks HOPO chain/leniency
                WasNoteGhosted = false; // Reset ghosted flag on miss
                EngineTimer.Reset(ref FrontEndExpireTime); // Reset ghost timer
            }
            else // AutoPlay miss (should be rare)
            {
                // Ensure player leniency timers are disabled/reset.
                HopoLeniencyTimer.Disable();
                StrumLeniencyTimer.Disable();
                EngineTimer.Reset(ref FrontEndExpireTime);
                WasNoteGhosted = false;
                 HasTapped = false;
            }

            // Call base.MissNote for combo reset, state updates, event firing.
            // Base MissNote now calls AdvanceToNextNote internally.
            base.MissNote(note);
        }

        /// <summary>
        /// Updates engine timers (Hopo Leniency, Strum Leniency).
        /// Prevents Overstrum penalty during AutoPlay or AutoStrum.
        /// </summary>
        protected void UpdateTimers()
        {
            bool isAutoPlay = IsAutoPlayActive();
            bool isAutoStrum = IsAutoStrumActive();

            // Update Hopo Leniency Timer (only relevant for manual play)
            if (!isAutoPlay && HopoLeniencyTimer.IsActive && HopoLeniencyTimer.IsExpired(CurrentTime))
            {
                HopoLeniencyTimer.Disable();
                ReRunHitLogic = true; // Timer state changed, requires logic re-run
            }

            // Update Strum Leniency Timer
            if (StrumLeniencyTimer.IsActive)
            {
                if (StrumLeniencyTimer.IsExpired(CurrentTime))
                {
                    // Only trigger Overstrum penalty if NOT in AutoPlay AND NOT in AutoStrum mode
                    if (!isAutoPlay && !isAutoStrum)
                    {
                        Overstrum(); // Overstrum method itself has an AutoPlay check now
                    }
                    // Always disable the timer once expired
                    StrumLeniencyTimer.Disable();
                    ReRunHitLogic = true; // Timer state changed, requires logic re-run
                }
            }
        }

        /// <summary>
        /// Checks for ghost inputs (incorrect hammer-ons). Disabled during AutoPlay.
        /// </summary>
        /// <param name="note">The note being checked against (usually Notes[NoteIndex]).</param>
        /// <returns>True if a ghost input is detected, false otherwise.</returns>
        protected bool CheckForGhostInput(GuitarNote note)
        {
            // Ghosting is a player input phenomenon, disable check during AutoPlay
            if (IsAutoPlayActive())
            {
                return false;
            }

            // Original conditions: Must have a previous note, must be a fret press action,
            // and the note must be somewhat relevant (in or near window).
            if (note.PreviousNote is null || !IsFretPress) return false;
             double backend = note.Time + EngineParameters.HitWindow.GetBackEnd(EngineParameters.HitWindow.CalculateHitWindow(GetAverageNoteDistance(note)));
            if (CurrentTime > backend) return false; // Too late for this note to be ghosted


            // Refined Ghost Logic: Detects a quick upward fret change (hammer-on)
            // where the resulting held frets do *not* correctly match the target note's required frets.
            int currentFrets = ButtonMask & ~OPEN_MASK;
            int lastFrets = LastButtonMask & ~OPEN_MASK;
            int targetFrets = note.NoteMask & ~OPEN_MASK;

            // No ghosting if no frets held, or if frets didn't change, or if target is nothing
            if (currentFrets == 0 || lastFrets == currentFrets || targetFrets == 0) return false;

            // Check for upward motion (MSB check is a decent proxy for "higher fret")
            bool isHammerOn = GetMostSignificantBit(currentFrets) > GetMostSignificantBit(lastFrets);

            // Ghosting = Hammer-on motion occurred, AND the frets now held DO NOT include all required frets for the target note.
            return isHammerOn && (currentFrets & targetFrets) != targetFrets;
        }

        /// <summary>
        /// Helper to find the index (1-based) of the most significant bit set in the mask.
        /// Used for comparing relative fret positions in ghost input checks.
        /// </summary>
        private static int GetMostSignificantBit(int mask)
        {
            if (mask == 0) return 0;
            // Efficient method using .NET built-ins if available (requires .NET Core 3.0+ / .NET 5+)
            // return System.Numerics.BitOperations.Log2((uint)mask) + 1;

            // Manual fallback implementation
            int msbIndex = 0;
            while (mask != 0) { mask >>= 1; msbIndex++; }
            return msbIndex; // Returns 1 for Green (mask 1), 2 for Red (mask 2), etc. (adjust if mask values differ)
        }

    } // End YargFiveFretEngine class
} // End namespace