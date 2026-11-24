// UC-T08-10: retained clock epoch, independent civil labels and actual local environment astronomy.
using System;
using UnityEngine;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Weather;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestEnvironmentGeography()
        {
            var root = new GameObject("UC local solar geography"); root.SetActive(false);
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false; world.worldSizeKm = 4;
                var weather = root.AddComponent<UCWeatherSystemManager>(); weather.enabled = false;
                weather.standardMeridianDeg = 30; weather.longitudeDeg = 30; weather.latitudeDeg = 0;
                typeof(UCWeatherSystemManager).GetField("clockReferenceCaptured", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(weather, false); // Simulate a retained pre-migration serialized asset.
                weather.SetCalendarTime(2, 172, 12);
                ClockState before = weather.ResolveClock(); double instant = weather.WorldInstantSeconds;
                CheckClose(before.hourAngleDeg, ClockMath.SolarHourAngleDeg(weather.SecondsOfDay, 30, before.equationOfTimeMin, 30),
                    "UC-T08-10 old assets retain their authored reference-meridian astronomy on first capture.");
                weather.standardMeridianDeg = -90;
                ClockState relabelled = weather.ResolveClock();
                Check(relabelled.hourAngleDeg == before.hourAngleDeg && relabelled.elevationDeg == before.elevationDeg && weather.WorldInstantSeconds == instant && weather.CivilDisplayHour == 4,
                    "UC-T08-10 a civil timezone label change moves only the display hour, retaining instant and sky.");
                weather.longitudeSpanDeg = 120; weather.latitudeSpanDeg = 20;
                ClockState west = weather.SampleEnvironmentAt(new Vector3(-2000, 0, 0)).Clock;
                ClockState east = weather.SampleEnvironmentAt(new Vector3(2000, 200, 0)).Clock;
                CheckClose(east.hourAngleDeg - west.hourAngleDeg, 120,
                    "UC-T08-10 widely separated longitudes at one instant use the same ClockMath solar correction.");
                var left = weather.SampleEnvironmentAt(new Vector3(-0.1f, 0, 0));
                var right = weather.SampleEnvironmentAt(new Vector3(0.1f, 0, 0));
                Check(Mathf.Abs(left.Clock.elevationDeg - right.Clock.elevationDeg) < 0.1f && left.WorldInstantSeconds == right.WorldInstantSeconds,
                    "UC-T08-10 neighbouring samples have continuous astronomy and one instant across civil label boundaries.");
                ClockState north = weather.ResolveClockAt(new Vector3(0, 0, 2000)), south = weather.ResolveClockAt(new Vector3(0, 0, -2000));
                Check(north.latitudeDeg == 10 && south.latitudeDeg == -10 && north.secondsOfDay == south.secondsOfDay,
                    "UC-T08-10 finite-world latitude uses the existing ecology ClockMath gradient and shared calendar.");
                weather.longitudeSpanDeg = weather.latitudeSpanDeg = 0;
                Check(weather.ResolveClockAt(new Vector3(99999, 200, -99999)).elevationDeg == before.elevationDeg,
                    "UC-T08-10 default zero spans preserve the single-site sky independently of camera movement.");
                bool invalid = false; try { weather.ResolveClockAt(new Vector3(float.NaN, 0, 0)); } catch (ArgumentOutOfRangeException) { invalid = true; }
                Check(invalid && weather.WorldInstantSeconds == instant, "UC-T08-10 invalid geography samples fail before changing the authoritative clock.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
