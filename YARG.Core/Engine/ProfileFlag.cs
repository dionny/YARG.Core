// YARG.Core/Engine/ProfileFlag.cs
using System;

namespace YARG.Core.Engine
{
    /// <summary>
    /// Defines boolean flags that can be toggled per player profile.
    /// </summary>
    [Flags] // Using Flags allows bitwise operations if ever needed, though we'll treat them individually for now.
    public enum ProfileFlag
    {
        None = 0,

        /// <summary>
        /// Automatically hits notes when the correct inputs are held, bypassing strum/hit requirements.
        /// Primarily affects Guitar/Drums modes.
        /// </summary>
        AutoStrum = 1 << 0,

        // Add other future flags here, e.g.:
        // Invincible = 1 << 1,
        // ScoreMultiplierLock = 1 << 2,
        AutoPlay = 1 << 1, // Add this flag
    }
}