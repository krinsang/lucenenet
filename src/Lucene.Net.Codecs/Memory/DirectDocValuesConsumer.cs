﻿/*
 * Licensed to the Apache Software Foundation (ASF) under one or more
 * contributor license agreements.  See the NOTICE file distributed with
 * this work for additional information regarding copyright ownership.
 * The ASF licenses this file to You under the Apache License, Version 2.0
 * (the "License"); you may not use this file except in compliance with
 * the License.  You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

namespace Lucene.Net.Codecs.Memory
{
    using System;
    using System.Diagnostics;
    using System.Collections.Generic;
   
    using FieldInfo = Index.FieldInfo;
    using IndexFileNames = Index.IndexFileNames;
    using SegmentWriteState = Index.SegmentWriteState;
    using IndexOutput = Store.IndexOutput;
    using BytesRef = Util.BytesRef;
    using IOUtils = Util.IOUtils;

    /// <summary>
    /// Writer for <seealso cref="DirectDocValuesFormat"/>
    /// </summary>
    internal class DirectDocValuesConsumer : DocValuesConsumer
    {
        internal IndexOutput data, meta;
        internal readonly int maxDoc;

        internal DirectDocValuesConsumer(SegmentWriteState state, string dataCodec, string dataExtension,
            string metaCodec, string metaExtension)
        {
            maxDoc = state.SegmentInfo.DocCount;
            bool success = false;
            try
            {
                string dataName = IndexFileNames.SegmentFileName(state.SegmentInfo.Name, state.SegmentSuffix,
                    dataExtension);
                data = state.Directory.CreateOutput(dataName, state.Context);
                CodecUtil.WriteHeader(data, dataCodec, VERSION_CURRENT);
                string metaName = IndexFileNames.SegmentFileName(state.SegmentInfo.Name, state.SegmentSuffix,
                    metaExtension);
                meta = state.Directory.CreateOutput(metaName, state.Context);
                CodecUtil.WriteHeader(meta, metaCodec, VERSION_CURRENT);
                success = true;
            }
            finally
            {
                if (!success)
                {
                    IOUtils.CloseWhileHandlingException(this);
                }
            }
        }

        public override void AddNumericField(FieldInfo field, IEnumerable<long> values)
        {
            meta.WriteVInt(field.Number);
            meta.WriteByte(NUMBER);
            AddNumericFieldValues(field, values);
        }

        private void AddNumericFieldValues(FieldInfo field, IEnumerable<long> values)
        {
            meta.WriteLong(data.FilePointer);
            long minValue = long.MaxValue;
            long maxValue = long.MinValue;
            bool missing = false;

            long count = 0;
            foreach (var nv in values)
            {
                if (nv != null)
                {
                    var v = nv;
                    minValue = Math.Min(minValue, v);
                    maxValue = Math.Max(maxValue, v);
                }
                else
                {
                    missing = true;
                }
                count++;
                if (count >= DirectDocValuesFormat.MAX_SORTED_SET_ORDS)
                {
                    throw new ArgumentException("DocValuesField \"" + field.name + "\" is too large, must be <= " +
                                                       DirectDocValuesFormat.MAX_SORTED_SET_ORDS + " values/total ords");
                }
            }
            meta.WriteInt((int) count);

            if (missing)
            {
                long start = data.FilePointer;
                WriteMissingBitset(values);
                meta.WriteLong(start);
                meta.WriteLong(data.FilePointer - start);
            }
            else
            {
                meta.WriteLong(-1L);
            }

            sbyte byteWidth;
            if (minValue >= sbyte.MinValue && maxValue <= sbyte.MaxValue)
            {
                byteWidth = 1;
            }
            else if (minValue >= short.MinValue && maxValue <= short.MaxValue)
            {
                byteWidth = 2;
            }
            else if (minValue >= int.MinValue && maxValue <= int.MaxValue)
            {
                byteWidth = 4;
            }
            else
            {
                byteWidth = 8;
            }
            meta.WriteByte(byteWidth);

            foreach (var nv in values)
            {
                long v;
                if (nv != null)
                {
                    v = (long) nv;
                }
                else
                {
                    v = 0;
                }

                switch (byteWidth)
                {
                    case 1:
                        data.WriteByte((sbyte) v);
                        break;
                    case 2:
                        data.WriteShort((short) v);
                        break;
                    case 4:
                        data.WriteInt((int) v);
                        break;
                    case 8:
                        data.WriteLong(v);
                        break;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) return;

            var success = false;
            try
            {
                if (meta != null)
                {
                    meta.WriteVInt(-1); // write EOF marker
                    CodecUtil.WriteFooter(meta); // write checksum
                }
                if (data != null)
                {
                    CodecUtil.WriteFooter(data);
                }
                success = true;
            }
            finally
            {
                if (success)
                {
                    IOUtils.Close(data, meta);
                }
                else
                {
                    IOUtils.CloseWhileHandlingException(data, meta);
                }
                data = meta = null;
            }
        }

        public override void AddBinaryField(FieldInfo field, IEnumerable<BytesRef> values)
        {
            meta.WriteVInt(field.Number);
            meta.WriteByte(BYTES);
            AddBinaryFieldValues(field, values);
        }

        private void AddBinaryFieldValues(FieldInfo field, IEnumerable<BytesRef> values)
        {
            // write the byte[] data
            long startFP = data.FilePointer;
            bool missing = false;
            long totalBytes = 0;
            int count = 0;
            foreach (BytesRef v in values)
            {
                if (v != null)
                {
                    data.WriteBytes(v.Bytes, v.Offset, v.Length);
                    totalBytes += v.Length;
                    if (totalBytes > DirectDocValuesFormat.MAX_TOTAL_BYTES_LENGTH)
                    {
                        throw new ArgumentException("DocValuesField \"" + field.name +
                                                           "\" is too large, cannot have more than DirectDocValuesFormat.MAX_TOTAL_BYTES_LENGTH (" +
                                                           DirectDocValuesFormat.MAX_TOTAL_BYTES_LENGTH + ") bytes");
                    }
                }
                else
                {
                    missing = true;
                }
                count++;
            }

            meta.WriteLong(startFP);
            meta.WriteInt((int) totalBytes);
            meta.WriteInt(count);
            if (missing)
            {
                long start = data.FilePointer;
                WriteMissingBitset(values);
                meta.WriteLong(start);
                meta.WriteLong(data.FilePointer - start);
            }
            else
            {
                meta.WriteLong(-1L);
            }

            int addr = 0;
            foreach (BytesRef v in values)
            {
                data.WriteInt(addr);
                if (v != null)
                {
                    addr += v.Length;
                }
            }
            data.WriteInt(addr);
        }

        // TODO: in some cases representing missing with minValue-1 wouldn't take up additional space and so on,
        // but this is very simple, and algorithms only check this for values of 0 anyway (doesnt slow down normal decode)
        internal virtual void WriteMissingBitset<T1>(IEnumerable<T1> values)
        {
            long bits = 0;
            int count = 0;
            foreach (object v in values)
            {
                if (count == 64)
                {
                    data.WriteLong(bits);
                    count = 0;
                    bits = 0;
                }
                if (v != null)
                {
                    bits |= 1L << (count & 0x3f);
                }
                count++;
            }
            if (count > 0)
            {
                data.WriteLong(bits);
            }
        }

        public override void AddSortedField(FieldInfo field, IEnumerable<BytesRef> values, IEnumerable<long> docToOrd)
        {
            meta.WriteVInt(field.Number);
            meta.WriteByte(SORTED);

            // write the ordinals as numerics
            AddNumericFieldValues(field, docToOrd);

            // write the values as binary
            AddBinaryFieldValues(field, values);
        }

        // note: this might not be the most efficient... but its fairly simple
        public override void AddSortedSetField(FieldInfo field, IEnumerable<BytesRef> values,
            IEnumerable<long> docToOrdCount, IEnumerable<long> ords)
        {
            meta.WriteVInt(field.Number);
            meta.WriteByte(SORTED_SET);

            // First write docToOrdCounts, except we "aggregate" the
            // counts so they turn into addresses, and add a final
            // value = the total aggregate:
            AddNumericFieldValues(field, new IterableAnonymousInnerClassHelper(this, docToOrdCount));

            // Write ordinals for all docs, appended into one big
            // numerics:
            AddNumericFieldValues(field, ords);

            // write the values as binary
            AddBinaryFieldValues(field, values);
        }

        private class IterableAnonymousInnerClassHelper : IEnumerable<Number>
        {
            private readonly DirectDocValuesConsumer _outerInstance;
            private readonly IEnumerable<long> _docToOrdCount;

            public IterableAnonymousInnerClassHelper(DirectDocValuesConsumer outerInstance,
                IEnumerable<long> docToOrdCount)
            {
                _outerInstance = outerInstance;
                _docToOrdCount = docToOrdCount;
            }


            // Just aggregates the count values so they become
            // "addresses", and adds one more value in the end
            // (the final sum):

            public virtual IEnumerator<long> GetEnumerator()
            {
                var iter = _docToOrdCount.GetEnumerator();
                return new IteratorAnonymousInnerClassHelper(this, iter);
            }

            private class IteratorAnonymousInnerClassHelper : IEnumerator<long>
            {
                private readonly IterableAnonymousInnerClassHelper outerInstance;
                private readonly IEnumerator<long> _iter;

                public IteratorAnonymousInnerClassHelper(IterableAnonymousInnerClassHelper outerInstance,
                    IEnumerator<long> iter)
                {
                    this.outerInstance = outerInstance;
                    _iter = iter;
                }


                internal long sum;
                internal bool ended;

                public virtual bool HasNext()
                {
                    return _iter.HasNext() || !ended;
                }

                public virtual long Next()
                {
                    long toReturn = sum;
                    if (_iter.hasNext())
                    {
                        long n = _iter.next();
                        if (n != null)
                        {
                            sum += n;
                        }
                    }
                    else if (!ended)
                    {
                        ended = true;
                    }
                    else
                    {
                        Debug.Assert(false);
                    }

                    return toReturn;
                }

                public virtual void Remove()
                {
                    throw new NotSupportedException();
                }
            }
        }
    }
}