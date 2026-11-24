using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

namespace UniverseCreator.Nodes
{
    // UC-T02-06: measure managed payload graphs once on publication, never per pixel or per frame.
    // Overlapping products can conservatively count shared children twice; native asset references are borrowed.
    internal static class ProductMemoryFootprint
    {
        private static readonly ConcurrentDictionary<Type, FieldInfo[]> fields = new ConcurrentDictionary<Type, FieldInfo[]>();
        private static readonly ConcurrentDictionary<Type, long> valueSizes = new ConcurrentDictionary<Type, long>();

        internal static long Estimate(object product)
        {
            var seen = new HashSet<object>(ProductMemoryBudget.Identity.Instance);
            return Measure(product, seen, 0);
        }

        private static long Measure(object value, HashSet<object> seen, int depth)
        {
            if (value == null) return 0;
            Type type = value.GetType();
            // Never invoke Unity equality, properties, native queries or disposal on a worker.
            if (value is UnityEngine.Object || value is Delegate || value is Type) return IntPtr.Size;
            if (value is TileData.TextureResourceOwner || value is ProductMemoryBudget.Lease) return IntPtr.Size; // UC-T07-02: resource pins borrow accounting owners; never count the entire budget through a product.
            if (type.IsPrimitive || type.IsEnum) return ValueSize(type);
            if (!type.IsValueType && !seen.Add(value)) return 0;
            if (depth > 64 || seen.Count > 100000)
                throw new InvalidOperationException("UC-T02-06: product payload exceeds bounded accounting traversal.");
            checked
            {
                if (value is string text) return 24L + 2L * text.Length;
                // UC-T07-02: immutable pixel storage has a fixed byte contract; avoid walking metadata on every consumer pin.
                if (value is TextureImageProduct image) return image.Descriptor.ManagedBytes;
                if (value is Array array)
                {
                    Type element = type.GetElementType();
                    long bytes = 32L + array.Rank * 8L + array.LongLength * ValueSize(element);
                    if (ContainsReferences(element))
                    {
                        // UC climate/memory repair: river ID grids have many slots but few shared immutable strings.
                        // Bound scalar-string scanning separately; recursive graphs retain their object/depth limits.
                        long slotLimit = element == typeof(string) ? 1048576 : 100000;
                        if (array.LongLength > slotLimit)
                            throw new InvalidOperationException("UC-T02-06: reference-array accounting exceeds the bounded traversal limit.");
                        foreach (object item in array) bytes += Measure(item, seen, depth + 1);
                    }
                    return bytes;
                }
                if (value is IDictionary dictionary)
                {
                    // Dictionary capacity remains below twice the occupied growth threshold in the production stores.
                    // Empty preallocated plugin dictionaries are instead walked through their actual backing fields below.
                    if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                        return MeasureFields(value, type, seen, depth);
                    long bytes = 64L + dictionary.Count * 72L;
                    foreach (DictionaryEntry item in dictionary)
                        bytes += Measure(item.Key, seen, depth + 1) + Measure(item.Value, seen, depth + 1);
                    return bytes;
                }
                if (value is IList list && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                {
                    int capacity = (int)type.GetProperty("Capacity").GetValue(value);
                    Type element = type.GetGenericArguments()[0];
                    long bytes = 64L + capacity * ValueSize(element);
                    if (ContainsReferences(element))
                    {
                        if (list.Count > 100000)
                            throw new InvalidOperationException("UC-T02-06: reference-list accounting exceeds the bounded traversal limit.");
                        foreach (object item in list) bytes += Measure(item, seen, depth + 1);
                    }
                    return bytes;
                }
                return MeasureFields(value, type, seen, depth);
            }
        }

        private static long MeasureFields(object value, Type type, HashSet<object> seen, int depth)
        {
            long bytes = type.IsValueType ? 0 : 32;
            foreach (FieldInfo field in Fields(type))
            {
                checked { bytes += field.FieldType.IsValueType ? ValueSize(field.FieldType) : IntPtr.Size; }
                if (ContainsReferences(field.FieldType)) checked { bytes += Measure(field.GetValue(value), seen, depth + 1); }
            }
            return bytes;
        }

        private static FieldInfo[] Fields(Type type) => fields.GetOrAdd(type, t =>
        {
            var result = new List<FieldInfo>();
            for (Type current = t; current != null; current = current.BaseType)
                result.AddRange(current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
            return result.ToArray();
        });

        private static bool ContainsReferences(Type type)
        {
            if (!type.IsValueType) return true;
            if (type.IsPrimitive || type.IsEnum) return false;
            foreach (FieldInfo field in Fields(type)) if (ContainsReferences(field.FieldType)) return true;
            return false;
        }

        private static long ValueSize(Type type) => valueSizes.GetOrAdd(type, t =>
        {
            if (!t.IsValueType) return (long)IntPtr.Size;
            if (t.IsEnum) return ValueSize(Enum.GetUnderlyingType(t));
            if (t == typeof(bool) || t == typeof(byte) || t == typeof(sbyte)) return 1;
            if (t == typeof(char) || t == typeof(short) || t == typeof(ushort)) return 2;
            if (t == typeof(int) || t == typeof(uint) || t == typeof(float)) return 4;
            if (t == typeof(long) || t == typeof(ulong) || t == typeof(double)) return 8;
            if (t == typeof(IntPtr) || t == typeof(UIntPtr)) return IntPtr.Size;
            long bytes = 0;
            foreach (FieldInfo field in Fields(t)) bytes += ValueSize(field.FieldType);
            return Math.Max(1, (bytes + 7) / 8 * 8); // Conservative eight-byte structure alignment.
        });
    }
}
