//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

#nullable enable

namespace YARG.Core.Chart
{
    /// <summary>
    /// Tracks the current event of an event list across ticks.
    /// </summary>
    public class ChartEventTickTracker<TEvent>
        where TEvent : ChartEvent
    {
        private List<TEvent> _events;
        private int _eventIndex = -1;

        public TEvent? Current => _eventIndex >= 0 ? _events[_eventIndex] : null;
        public int CurrentIndex => _eventIndex;

        public ChartEventTickTracker(List<TEvent> events)
        {
            _events = events;
        }

        /// <summary>
        /// Updates the state of the event tracker to the given tick.
        /// </summary>
        /// <returns>
        /// True if a new event has been reached, false otherwise.
        /// </returns>
        public bool Update(uint tick)
        {
            int previousIndex = _eventIndex;
            while (_eventIndex + 1 < _events.Count && _events[_eventIndex + 1].Tick <= tick)
                _eventIndex++;
            return previousIndex != _eventIndex;
        }

        /// <summary>
        /// Updates the state of the event tracker to the given tick by a single event.
        /// </summary>
        /// <returns>
        /// True if a new event has been reached, false otherwise.
        /// </returns>
        public bool UpdateOnce(uint tick, [NotNullWhen(true)] out TEvent? current)
        {
            if (_eventIndex + 1 < _events.Count && _events[_eventIndex + 1].Tick <= tick)
            {
                _eventIndex++;
                current = _events[_eventIndex];
                return true;
            }

            current = Current;
            return false;
        }

        /// <summary>
        /// Resets the state of the event tracker.
        /// </summary>
        public void Reset()
        {
            _eventIndex = -1;
        }

        /// <summary>
        /// Resets the state of the event tracker to the given tick.
        /// </summary>
        public void ResetToTick(uint tick)
        {
            _eventIndex = _events.LowerBound(tick);
        }
    }

    /// <summary>
    /// Tracks the current event of an event list across times.
    /// </summary>
    public class ChartEventTimeTracker<TEvent>
        where TEvent : ChartEvent
    {
        private List<TEvent> _events;
        private int _eventIndex = -1;

        public TEvent? Current => _eventIndex >= 0 ? _events[_eventIndex] : null;
        public int CurrentIndex => _eventIndex;

        public ChartEventTimeTracker(List<TEvent> events)
        {
            _events = events;
        }

        /// <summary>
        /// Updates the state of the event tracker to the given time.
        /// </summary>
        /// <returns>
        /// True if a new event has been reached, false otherwise.
        /// </returns>
        public bool Update(double time)
        {
            int previousIndex = _eventIndex;
            while (_eventIndex + 1 < _events.Count && _events[_eventIndex + 1].Time <= time)
                _eventIndex++;
            return previousIndex != _eventIndex;
        }

        /// <summary>
        /// Updates the state of the event tracker to the given time by a single event.
        /// </summary>
        /// <returns>
        /// True if a new event has been reached, false otherwise.
        /// </returns>
        public bool UpdateOnce(double time, [NotNullWhen(true)] out TEvent? current)
        {
            if (_eventIndex + 1 < _events.Count && _events[_eventIndex + 1].Time <= time)
            {
                _eventIndex++;
                current = _events[_eventIndex];
                return true;
            }

            current = Current;
            return false;
        }

        /// <summary>
        /// Resets the state of the event tracker.
        /// </summary>
        public void Reset()
        {
            _eventIndex = -1;
        }

        /// <summary>
        /// Resets the state of the event tracker to the given time.
        /// </summary>
        public void ResetToTime(double time)
        {
            _eventIndex = _events.LowerBound(time);
        }
    }

}