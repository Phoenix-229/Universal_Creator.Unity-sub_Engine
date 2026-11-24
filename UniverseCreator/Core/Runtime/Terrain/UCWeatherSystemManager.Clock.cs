// UC-T08-02: extend the existing weather-owned clock; no second timer or world manager.
using System;
using UnityEngine;
using UniverseCreator.Weather;

namespace UniverseCreator.TerrainSystem
{
    public partial class UCWeatherSystemManager
    {
        /// <summary>Last supported model year, inclusive; the retained calendar has 365 days per year and no leap-day model.</summary>
        public const int MaximumCalendarYear = 9999;
        private const double ModelYearSeconds = 365d * ClockState.SecondsPerDay;
        private const double MaximumWorldInstantSeconds = MaximumCalendarYear * ModelYearSeconds - 1;
        [SerializeField, HideInInspector] private double clockSummationError;

        /// <summary>Current world instant in simulated seconds since day 1 of model year 1; all geography samples share this instant.</summary>
        public double WorldInstantSeconds => AbsoluteSeconds;
        /// <summary>UC-T08-11: main-thread discontinuity revision for camera histories; regular clock ticks do not invalidate temporal rendering.</summary>
        internal long EnvironmentHistoryRevision { get; private set; }

        /// <summary>Seeks the existing clock without changing its authored start settings; paused clocks may also be sought.</summary>
        /// <param name="absoluteSeconds">Finite seconds since model year 1, from zero through the final second of year 9999.</param>
        /// <exception cref="ArgumentOutOfRangeException">The requested instant is nonfinite or outside the supported calendar; state is retained.</exception>
        /// <remarks>Main thread. Resets transient weather smoothing and invalidates season readers; astronomy remains owned by ClockMath.</remarks>
        public void SeekWorldInstant(double absoluteSeconds)
        {
            if (double.IsNaN(absoluteSeconds) || double.IsInfinity(absoluteSeconds) || absoluteSeconds < 0 || absoluteSeconds > MaximumWorldInstantSeconds)
                throw new ArgumentOutOfRangeException(nameof(absoluteSeconds));
            elapsedSimSeconds = absoluteSeconds - AuthoredClockStartSeconds(); NotifyClockSeek();
        }

        /// <summary>Seeks a model calendar date on the same authoritative clock, preserving its authored start fields.</summary>
        /// <param name="year">Model year 1…9999.</param>
        /// <param name="dayOfYear">Model day 1…365, without leap days.</param>
        /// <param name="hourOfDay">Finite civil hour in [0,24).</param>
        /// <exception cref="ArgumentOutOfRangeException">A date/hour is outside its documented range; state is retained.</exception>
        public void SetCalendarTime(int year, int dayOfYear, float hourOfDay)
        {
            if (year < 1 || year > MaximumCalendarYear) throw new ArgumentOutOfRangeException(nameof(year));
            if (dayOfYear < 1 || dayOfYear > 365) throw new ArgumentOutOfRangeException(nameof(dayOfYear));
            if (float.IsNaN(hourOfDay) || float.IsInfinity(hourOfDay) || hourOfDay < 0 || hourOfDay >= 24) throw new ArgumentOutOfRangeException(nameof(hourOfDay));
            SeekWorldInstant((year - 1) * ModelYearSeconds + (dayOfYear - 1) * (double)ClockState.SecondsPerDay + hourOfDay * (double)ClockState.SecondsPerHour);
        }

        /// <summary>Steps the existing clock by real elapsed time when enabled, unpaused and configured with a finite positive rate.</summary>
        /// <param name="realSeconds">Finite nonnegative elapsed real seconds; the configured timeScale converts these to simulated seconds.</param>
        /// <exception cref="ArgumentOutOfRangeException">The elapsed time is negative or nonfinite; state is retained.</exception>
        /// <remarks>Main thread. Uses compensated summation, saturates at the supported calendar endpoint and performs no native renderer work.</remarks>
        public void TickWorldClock(double realSeconds)
        {
            if (double.IsNaN(realSeconds) || double.IsInfinity(realSeconds) || realSeconds < 0) throw new ArgumentOutOfRangeException(nameof(realSeconds));
            if (!ClockIsRunning || realSeconds == 0) return;
            double remaining = MaximumWorldInstantSeconds - AbsoluteSeconds;
            AdvanceClockCore(realSeconds >= remaining / timeScale ? remaining : realSeconds * timeScale);
        }

        private double AuthoredClockStartSeconds() => (startDayOfYear - 1) * (double)ClockState.SecondsPerDay + startHourOfDay * (double)ClockState.SecondsPerHour;

        private void AdvanceClockCore(double simulatedSeconds)
        {
            double instant = AbsoluteSeconds, start = AuthoredClockStartSeconds();
            if (simulatedSeconds >= MaximumWorldInstantSeconds - instant)
            { elapsedSimSeconds = MaximumWorldInstantSeconds - start; clockSummationError = 0; return; }
            if (simulatedSeconds <= -instant)
            { elapsedSimSeconds = -start; clockSummationError = 0; return; }
            double corrected = simulatedSeconds - clockSummationError;
            double next = elapsedSimSeconds + corrected;
            clockSummationError = (next - elapsedSimSeconds) - corrected; elapsedSimSeconds = next;
        }

        private void NotifyClockSeek()
        {
            EnvironmentHistoryRevision++;
            clockSummationError = 0; _weatherMachineReady = false; _weatherMachine = null;
            LightingOrNull?.ResetEnvironmentLighting(); // UC-T08-07: deterministic seeks retire exposure history, never integrate a second lighting clock.
            ResetCameraPrecipitation(); // UC-T08-08: seeking cannot retain old world-space drops or flakes.
            ResetSurfaceStores(); // UC-T08-09: deterministic seek starts from authored stores, without inventing a weather replay.
            UCVegetationSystemManager.ForWorld(transform)?.MarkAppearanceDirty(); // UC-T08-04: seeking updates the standing forest, never placement inputs.
        }

        private void ValidateClockInstant()
        {
            startDayOfYear = Mathf.Clamp(startDayOfYear, 1, 365);
            if (float.IsNaN(startHourOfDay) || float.IsInfinity(startHourOfDay)) startHourOfDay = 0;
            double instant = AbsoluteSeconds;
            if (double.IsNaN(instant) || double.IsInfinity(instant)) elapsedSimSeconds = 0;
            else elapsedSimSeconds = Math.Max(0, Math.Min(MaximumWorldInstantSeconds, instant)) - AuthoredClockStartSeconds();
            if (double.IsNaN(clockSummationError) || double.IsInfinity(clockSummationError)) clockSummationError = 0;
        }
    }
}
