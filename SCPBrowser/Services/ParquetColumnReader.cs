using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Parquet;
using Parquet.Schema;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Reads one column of a Parquet row group into a flat array of the shape Parquet.Net 5's DataColumn.Data had:
    /// string[] for text, T[] for a required primitive, T?[] for an optional one. Parquet.Net 6 replaced
    /// ReadColumnAsync with typed reads into caller-owned memory; keeping the 5.x shape here leaves every consumer's
    /// handling of the values (and so every number derived from them) unchanged.
    /// </summary>
    internal static class ParquetColumnReader
    {
        /// <param name="pool">
        /// Optional string pool for text columns. DIA-NN reports repeat a few thousand run, protein and gene names
        /// over a million rows; with a pool each distinct value is allocated once instead of once per row.
        /// </param>
        public static async Task<Array> ReadAsync(ParquetRowGroupReader rowGroup, DataField field, StringPool pool = null)
        {
            int n = checked((int)rowGroup.RowCount);
            Type t = field.ClrType;

            // Parquet.Net 6 describes text columns as ReadOnlyMemory<char>; the string overload decodes them as UTF-8.
            if (t == typeof(string) || t == typeof(ReadOnlyMemory<char>))
            {
                var values = new string[n];
                if (pool != null && !field.IsNullable)
                {
                    // Decoded as slices of one buffer, then turned into strings only for values not seen before.
                    // A nullable column keeps the string path, which is the one that distinguishes null from "".
                    var slices = new ReadOnlyMemory<char>[n];
                    await rowGroup.ReadAsync<ReadOnlyMemory<char>>(field, slices.AsMemory());
                    for (int i = 0; i < n; i++)
                        values[i] = pool.Get(slices[i].Span);
                    return values;
                }

                await rowGroup.ReadAsync(field, values.AsMemory());
                return values;
            }

            if (t == typeof(float)) return await ReadStructAsync<float>(rowGroup, field, n);
            if (t == typeof(double)) return await ReadStructAsync<double>(rowGroup, field, n);
            if (t == typeof(int)) return await ReadStructAsync<int>(rowGroup, field, n);
            if (t == typeof(long)) return await ReadStructAsync<long>(rowGroup, field, n);
            if (t == typeof(short)) return await ReadStructAsync<short>(rowGroup, field, n);
            if (t == typeof(byte)) return await ReadStructAsync<byte>(rowGroup, field, n);
            if (t == typeof(bool)) return await ReadStructAsync<bool>(rowGroup, field, n);
            if (t == typeof(decimal)) return await ReadStructAsync<decimal>(rowGroup, field, n);
            if (t == typeof(DateTime)) return await ReadStructAsync<DateTime>(rowGroup, field, n);

            throw new NotSupportedException(
                $"Parquet column '{field.Name}' has type {t.Name}, which SCPBrowser does not read.");
        }

        private static async Task<Array> ReadStructAsync<T>(ParquetRowGroupReader rowGroup, DataField field, int n)
            where T : struct
        {
            if (field.IsNullable)
            {
                var nullable = new T?[n];
                await rowGroup.ReadAsync<T>(field, nullable.AsMemory());
                return nullable;
            }

            var values = new T[n];
            await rowGroup.ReadAsync<T>(field, values.AsMemory());
            return values;
        }
    }

    /// <summary>Equal text values share one string instance; lookups by span allocate nothing.</summary>
    internal sealed class StringPool
    {
        private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _bySpan;

        public StringPool()
        {
            _bySpan = _strings.GetAlternateLookup<ReadOnlySpan<char>>();
        }

        public string Get(ReadOnlySpan<char> value)
        {
            if (_bySpan.TryGetValue(value, out var existing))
                return existing;
            var created = new string(value);
            _strings.Add(created, created);
            return created;
        }
    }
}
