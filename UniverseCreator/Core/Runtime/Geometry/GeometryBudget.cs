// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    GeometryBudget.cs
//  Purpose: Measure before deciding (§ 9.10 G-62). The governor degrades the view only when
//           the budget is genuinely exceeded, and "genuinely" means a ROLLING MEDIAN, not an
//           instantaneous reading. Frame time, triangle count, draw-call count and resident
//           page/atlas memory are each tracked over a window; the median is compared against
//           the tier ceiling. A single GC hitch or one heavy frame spikes the mean but barely
//           moves the median, so it must not trigger a degradation - which would otherwise
//           read as the renderer flinching at noise.
//
//  MEDIAN, NOT MEAN - THAT IS THE WHOLE POINT
//  ------------------------------------------
//  The mean of a window is dragged by one 200 ms outlier; the median of an odd window ignores it
//  entirely, sitting on the central sample. `RollingMedian` keeps a fixed-capacity ring of the
//  last N samples and returns the middle of the sorted copy, so admitting a spike evicts the
//  oldest good sample and leaves the median unmoved. That is the test the section pins.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  Numbers and ceilings; no Time.deltaTime, no profiler, no debug window. The scene layer feeds
//  the four real measurements in each frame and draws the readout. The self-test pins the median
//  arithmetic, the spike immunity and the per-metric ceiling comparison.
// -----------------------------------------------------------------------------

using System;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>A fixed-capacity rolling median: the last N samples, median (not mean) so one outlier is ignored.</summary>
    public sealed class RollingMedian
    {
        private readonly float[] _buf;
        // UC-T04-11: one owner reuses sorting storage; repeated governor reads must not allocate per frame.
        private readonly float[] _sorted;
        private int _count;
        private int _head;
        private bool _dirty;
        private float _median;

        public RollingMedian(int capacity)
        {
            _buf = new float[capacity < 1 ? 1 : capacity];
            _sorted = new float[_buf.Length];
        }

        public int Count => _count;
        public int Capacity => _buf.Length;

        /// <summary>Add a sample, evicting the oldest once the window is full.</summary>
        public void Add(float value)
        {
            _buf[_head] = value;
            _head = (_head + 1) % _buf.Length;
            if (_count < _buf.Length) _count++;
            _dirty = true;
        }

        /// <summary>The median of the samples in the window (0 when empty). Odd count → the middle sample; even →
        /// the mean of the two middle samples. UC-T04-11: cached until the next Add; instance ownership is thread-confined.</summary>
        public float Median()
        {
            if (_count == 0) return 0f;
            if (!_dirty) return _median;
            Array.Copy(_buf, _sorted, _count);
            Array.Sort(_sorted, 0, _count);
            int mid = _count / 2;
            _median = (_count & 1) == 1 ? _sorted[mid] : (float)(0.5 * ((double)_sorted[mid - 1] + _sorted[mid]));
            _dirty = false;
            return _median;
        }
    }

    /// <summary>
    /// Rolling-median budget over the four cost metrics (§ 9.10 G-62): frame time, triangles, draw calls and
    /// resident memory, each compared against a tier ceiling. Median, not mean, so a single spike frame changes
    /// nothing. Device-free.
    /// </summary>
    public sealed class GeometryBudget
    {
        private readonly RollingMedian _frameMs, _triangles, _drawCalls, _memoryMB;
        private readonly float _frameMsCeiling, _triangleCeiling, _drawCallCeiling, _memoryMBCeiling;

        public GeometryBudget(int windowFrames, float frameMsCeiling, float triangleCeiling,
                              float drawCallCeiling, float memoryMBCeiling)
        {
            _frameMs = new RollingMedian(windowFrames);
            _triangles = new RollingMedian(windowFrames);
            _drawCalls = new RollingMedian(windowFrames);
            _memoryMB = new RollingMedian(windowFrames);
            _frameMsCeiling = frameMsCeiling; _triangleCeiling = triangleCeiling;
            _drawCallCeiling = drawCallCeiling; _memoryMBCeiling = memoryMBCeiling;
        }

        /// <summary>Record one frame's four measurements.</summary>
        public void Sample(float frameMs, float triangles, float drawCalls, float memoryMB)
        {
            _frameMs.Add(frameMs); _triangles.Add(triangles);
            _drawCalls.Add(drawCalls); _memoryMB.Add(memoryMB);
        }

        public float MedianFrameMs => _frameMs.Median();
        public float MedianTriangles => _triangles.Median();
        public float MedianDrawCalls => _drawCalls.Median();
        public float MedianMemoryMB => _memoryMB.Median();

        /// <summary>Whether any metric's median is over its tier ceiling — the budget-exceeded signal that G-63's
        /// degradation sequence responds to.</summary>
        public bool IsOverBudget()
        {
            return _frameMs.Median() > _frameMsCeiling
                || _triangles.Median() > _triangleCeiling
                || _drawCalls.Median() > _drawCallCeiling
                || _memoryMB.Median() > _memoryMBCeiling;
        }
    }
}
