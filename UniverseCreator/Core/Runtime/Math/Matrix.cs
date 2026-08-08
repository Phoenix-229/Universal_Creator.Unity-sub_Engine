// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    Matrix.cs
//  Purpose: 2D float heightfield with a CoordRect window and world-space mapping.
//
//  NOTE ON SCOPE: the Step 1 file list does not include a CoordRect.cs, but the
//  Matrix spec requires "CoordRect (offset + size)". Coord and CoordRect are
//  therefore declared here alongside their only consumer rather than adding
//  files the spec did not list.
//
//  INDEXING CONVENTION
//  -------------------
//  Flat index = (z - offset.z) * size.x + (x - offset.x).
//  So index +/- 1 steps along X, and index +/- size.x steps along Z. Every
//  neighbourhood helper in Erosion and MatrixOperations depends on this.
//
//  THREAD SAFETY: a Matrix is plain mutable data with no engine calls. Safe to
//  create and mutate on background threads; not safe to share for concurrent
//  writes without external synchronisation.
// -----------------------------------------------------------------------------

using System;
using UnityEngine;

namespace UniverseCreator
{
    /// <summary>Integer 2D coordinate on the heightfield lattice.</summary>
    [Serializable]
    public struct Coord : IEquatable<Coord>
    {
        public int x;
        public int z;

        public Coord(int x, int z) { this.x = x; this.z = z; }

        public static Coord Zero { get { return new Coord(0, 0); } }

        public static Coord operator +(Coord a, Coord b) { return new Coord(a.x + b.x, a.z + b.z); }
        public static Coord operator -(Coord a, Coord b) { return new Coord(a.x - b.x, a.z - b.z); }
        public static bool operator ==(Coord a, Coord b) { return a.x == b.x && a.z == b.z; }
        public static bool operator !=(Coord a, Coord b) { return a.x != b.x || a.z != b.z; }

        public bool Equals(Coord other) { return x == other.x && z == other.z; }
        public override bool Equals(object obj) { return obj is Coord && Equals((Coord)obj); }
        public override int GetHashCode() { return x * 397 ^ z; }
        public override string ToString() { return "(" + x + ", " + z + ")"; }
    }

    /// <summary>
    /// A rectangular window on the lattice: where it starts and how big it is.
    /// </summary>
    [Serializable]
    public struct CoordRect
    {
        public Coord offset;
        public Coord size;

        public CoordRect(int offsetX, int offsetZ, int sizeX, int sizeZ)
        {
            offset = new Coord(offsetX, offsetZ);
            size = new Coord(sizeX, sizeZ);
        }

        public CoordRect(Coord offset, Coord size)
        {
            this.offset = offset;
            this.size = size;
        }

        /// <summary>Total pixel count.</summary>
        public int Count { get { return size.x * size.z; } }

        /// <summary>One past the last coordinate on each axis.</summary>
        public Coord Max { get { return new Coord(offset.x + size.x, offset.z + size.z); } }

        public bool Contains(int x, int z)
        {
            return x >= offset.x && z >= offset.z &&
                   x < offset.x + size.x && z < offset.z + size.z;
        }

        public bool Contains(Coord c) { return Contains(c.x, c.z); }

        public override string ToString() { return "offset" + offset + " size" + size; }
    }

    /// <summary>
    /// A 2D float field with an associated world-space placement. This is the
    /// single data currency passed between height-producing nodes.
    /// </summary>
    [Serializable]
    public class Matrix
    {
        /// <summary>Flat backing store. See the indexing convention in the file header.</summary>
        public float[] arr;

        /// <summary>Lattice window this matrix covers.</summary>
        public CoordRect rect;

        /// <summary>World-space position of the matrix origin (rect.offset).</summary>
        public Vector3 worldPos;

        /// <summary>World-space extent the matrix spans on X and Z. Y is unused.</summary>
        public Vector3 worldSize;

        /// <summary>World height that a stored value of 1.0 corresponds to, in metres.</summary>
        public float worldHeight = 1f;

        /// <summary>Pixel count.</summary>
        public int Count { get { return arr.Length; } }

        // ---------------------------------------------------------------------
        //  Construction
        // ---------------------------------------------------------------------

        public Matrix(CoordRect rect)
        {
            this.rect = rect;
            this.arr = new float[rect.Count];
        }

        public Matrix(int sizeX, int sizeZ) : this(new CoordRect(0, 0, sizeX, sizeZ)) { }

        public Matrix(CoordRect rect, float[] existingArray)
        {
            if (existingArray == null)
                throw new ArgumentNullException("existingArray");
            if (existingArray.Length != rect.Count)
                throw new ArgumentException("Array length " + existingArray.Length +
                                            " does not match rect count " + rect.Count + ".");

            this.rect = rect;
            this.arr = existingArray;
        }

        /// <summary>
        /// Copy constructor. Deep-copies the data and carries the world placement
        /// across, so modifiers can work on a copy without disturbing the source
        /// a fan-out connection may still be feeding to other nodes.
        /// </summary>
        public Matrix(Matrix source)
        {
            if (source == null) throw new ArgumentNullException("source");

            rect = source.rect;
            worldPos = source.worldPos;
            worldSize = source.worldSize;
            worldHeight = source.worldHeight;

            arr = new float[source.arr.Length];
            Array.Copy(source.arr, arr, source.arr.Length);
        }

        /// <summary>Allocates an empty matrix with this one's shape and world placement.</summary>
        public Matrix Blank()
        {
            Matrix m = new Matrix(rect);
            m.worldPos = worldPos;
            m.worldSize = worldSize;
            m.worldHeight = worldHeight;
            return m;
        }

        /// <summary>Deep copy. Equivalent to the copy constructor.</summary>
        public Matrix Copy()
        {
            return new Matrix(this);
        }

        // ---------------------------------------------------------------------
        //  Indexing
        // ---------------------------------------------------------------------

        /// <summary>Flat index for a lattice coordinate. No bounds checking.</summary>
        public int GetIndex(int x, int z)
        {
            return (z - rect.offset.z) * rect.size.x + (x - rect.offset.x);
        }

        /// <summary>Access by lattice coordinate.</summary>
        public float this[int x, int z]
        {
            get { return arr[(z - rect.offset.z) * rect.size.x + (x - rect.offset.x)]; }
            set { arr[(z - rect.offset.z) * rect.size.x + (x - rect.offset.x)] = value; }
        }

        /// <summary>Access by lattice coordinate, returning 0 outside the rect.</summary>
        public float GetSafe(int x, int z)
        {
            if (!rect.Contains(x, z)) return 0f;
            return arr[(z - rect.offset.z) * rect.size.x + (x - rect.offset.x)];
        }

        /// <summary>
        /// Access by lattice coordinate, clamping to the edge outside the rect.
        /// This is the right choice for filter kernels, which would otherwise
        /// darken the borders.
        /// </summary>
        public float GetClamped(int x, int z)
        {
            if (x < rect.offset.x) x = rect.offset.x;
            if (z < rect.offset.z) z = rect.offset.z;
            if (x >= rect.offset.x + rect.size.x) x = rect.offset.x + rect.size.x - 1;
            if (z >= rect.offset.z + rect.size.z) z = rect.offset.z + rect.size.z - 1;

            return arr[(z - rect.offset.z) * rect.size.x + (x - rect.offset.x)];
        }

        // ---------------------------------------------------------------------
        //  Whole-field operations
        // ---------------------------------------------------------------------

        public void Fill(float value)
        {
            for (int i = 0; i < arr.Length; i++) arr[i] = value;
        }

        /// <summary>Copies another matrix's values in. Shapes must match.</summary>
        public void Fill(Matrix source)
        {
            AssertSameShape(source, "Fill");
            Array.Copy(source.arr, arr, arr.Length);
        }

        public void Clamp01()
        {
            for (int i = 0; i < arr.Length; i++)
            {
                float v = arr[i];
                if (v < 0f) arr[i] = 0f;
                else if (v > 1f) arr[i] = 1f;
            }
        }

        public void Clamp(float min, float max)
        {
            for (int i = 0; i < arr.Length; i++)
            {
                float v = arr[i];
                if (v < min) arr[i] = min;
                else if (v > max) arr[i] = max;
            }
        }

        public void Multiply(float factor)
        {
            for (int i = 0; i < arr.Length; i++) arr[i] *= factor;
        }

        public void Multiply(Matrix other)
        {
            AssertSameShape(other, "Multiply");
            for (int i = 0; i < arr.Length; i++) arr[i] *= other.arr[i];
        }

        public void Add(float value)
        {
            for (int i = 0; i < arr.Length; i++) arr[i] += value;
        }

        public void Add(Matrix other)
        {
            AssertSameShape(other, "Add");
            for (int i = 0; i < arr.Length; i++) arr[i] += other.arr[i];
        }

        public void Subtract(Matrix other)
        {
            AssertSameShape(other, "Subtract");
            for (int i = 0; i < arr.Length; i++) arr[i] -= other.arr[i];
        }

        /// <summary>Inverts about 1.0, i.e. v -> 1 - v.</summary>
        public void Invert()
        {
            for (int i = 0; i < arr.Length; i++) arr[i] = 1f - arr[i];
        }

        /// <summary>Inverts about an arbitrary reference level.</summary>
        public void Invert(float aboutLevel)
        {
            for (int i = 0; i < arr.Length; i++) arr[i] = 2f * aboutLevel - arr[i];
        }

        /// <summary>Linear blend toward another matrix. Shapes must match.</summary>
        public void Blend(Matrix other, float t)
        {
            AssertSameShape(other, "Blend");
            for (int i = 0; i < arr.Length; i++)
                arr[i] = arr[i] + (other.arr[i] - arr[i]) * t;
        }

        public float Max()
        {
            if (arr.Length == 0) return 0f;
            float max = arr[0];
            for (int i = 1; i < arr.Length; i++) if (arr[i] > max) max = arr[i];
            return max;
        }

        public float Min()
        {
            if (arr.Length == 0) return 0f;
            float min = arr[0];
            for (int i = 1; i < arr.Length; i++) if (arr[i] < min) min = arr[i];
            return min;
        }

        public float Average()
        {
            if (arr.Length == 0) return 0f;
            double sum = 0;
            for (int i = 0; i < arr.Length; i++) sum += arr[i];
            return (float)(sum / arr.Length);
        }

        /// <summary>True if any cell is NaN or infinite. Used to guard erosion output.</summary>
        public bool HasInvalidValues()
        {
            for (int i = 0; i < arr.Length; i++)
                if (float.IsNaN(arr[i]) || float.IsInfinity(arr[i])) return true;
            return false;
        }

        // ---------------------------------------------------------------------
        //  Sampling
        // ---------------------------------------------------------------------

        /// <summary>
        /// Bilinearly interpolated sample at fractional lattice coordinates.
        /// Edge-clamped, so sampling slightly outside the rect is well defined.
        /// </summary>
        public float GetInterpolated(float x, float z)
        {
            int xi = x > 0 ? (int)x : (int)x - 1;
            int zi = z > 0 ? (int)z : (int)z - 1;

            float xf = x - xi;
            float zf = z - zi;

            float v00 = GetClamped(xi, zi);
            float v10 = GetClamped(xi + 1, zi);
            float v01 = GetClamped(xi, zi + 1);
            float v11 = GetClamped(xi + 1, zi + 1);

            return InterpolationMath.Bilinear(v00, v10, v01, v11, xf, zf);
        }

        /// <summary>Bicubic sample at fractional lattice coordinates. Edge-clamped.</summary>
        public float GetInterpolatedCubic(float x, float z)
        {
            int xi = x > 0 ? (int)x : (int)x - 1;
            int zi = z > 0 ? (int)z : (int)z - 1;

            float xf = x - xi;
            float zf = z - zi;

            float[] p = new float[16];
            for (int dz = -1; dz <= 2; dz++)
                for (int dx = -1; dx <= 2; dx++)
                    p[(dz + 1) * 4 + (dx + 1)] = GetClamped(xi + dx, zi + dz);

            return InterpolationMath.Bicubic(p, xf, zf);
        }

        // ---------------------------------------------------------------------
        //  World-space mapping
        // ---------------------------------------------------------------------

        /// <summary>
        /// World distance covered by one pixel step. Uses (size - 1) intervals to
        /// match Unity's heightmap convention, where a 257-sample map spans the
        /// terrain width in 256 steps.
        /// </summary>
        public Vector2 PixelSize
        {
            get
            {
                float dx = rect.size.x > 1 ? worldSize.x / (rect.size.x - 1) : worldSize.x;
                float dz = rect.size.z > 1 ? worldSize.z / (rect.size.z - 1) : worldSize.z;
                return new Vector2(dx, dz);
            }
        }

        /// <summary>
        /// Converts a lattice coordinate to a world position. Y carries the
        /// stored height scaled by worldHeight.
        /// </summary>
        public Vector3 PixelToWorld(int x, int z)
        {
            Vector2 ps = PixelSize;
            float wx = worldPos.x + (x - rect.offset.x) * ps.x;
            float wz = worldPos.z + (z - rect.offset.z) * ps.y;
            float wy = worldPos.y + GetSafe(x, z) * worldHeight;
            return new Vector3(wx, wy, wz);
        }

        /// <summary>
        /// Converts a world XZ position to fractional lattice coordinates. Feed
        /// the result to GetInterpolated for a world-space height query.
        /// </summary>
        public Vector2 WorldToPixel(float worldX, float worldZ)
        {
            Vector2 ps = PixelSize;
            float px = ps.x > 1e-9f ? (worldX - worldPos.x) / ps.x + rect.offset.x : rect.offset.x;
            float pz = ps.y > 1e-9f ? (worldZ - worldPos.z) / ps.y + rect.offset.z : rect.offset.z;
            return new Vector2(px, pz);
        }

        /// <summary>Interpolated height at a world XZ position, in world metres.</summary>
        public float GetWorldHeight(float worldX, float worldZ)
        {
            Vector2 p = WorldToPixel(worldX, worldZ);
            return worldPos.y + GetInterpolated(p.x, p.y) * worldHeight;
        }

        /// <summary>Copies the world placement from another matrix.</summary>
        public void CopyWorldPlacement(Matrix source)
        {
            worldPos = source.worldPos;
            worldSize = source.worldSize;
            worldHeight = source.worldHeight;
        }

        // ---------------------------------------------------------------------
        //  Internal
        // ---------------------------------------------------------------------

        private void AssertSameShape(Matrix other, string operation)
        {
            if (other == null)
                throw new ArgumentNullException("other", operation + ": operand matrix is null.");
            if (other.arr.Length != arr.Length)
                throw new ArgumentException(operation + ": shape mismatch. This is " +
                                            rect + " (" + arr.Length + " px), operand is " +
                                            other.rect + " (" + other.arr.Length + " px).");
        }

        public override string ToString()
        {
            return "Matrix " + rect + " worldPos" + worldPos + " worldSize" + worldSize;
        }
    }
}
