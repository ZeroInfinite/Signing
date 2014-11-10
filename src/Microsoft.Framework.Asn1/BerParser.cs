﻿using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.Framework.Asn1
{
    using Subparser = Func<BerParser, BerHeader, Asn1Value>;

    public class BerParser
    {
        private BinaryReader _reader;

        private static readonly Dictionary<int, Subparser> _parsers = new Dictionary<int, Subparser>()
        {
            { Asn1Constants.Tags.Sequence, (p, h) => p.ParseSequence(h) },
            { Asn1Constants.Tags.ObjectIdentifier, (p, h) => p.ParseOid(h) }
        };

        public BerParser(byte[] input)
            : this(new MemoryStream(input))
        {
        }

        public BerParser(Stream input)
            : this(new BinaryReader(input, Encoding.UTF8, leaveOpen: false))
        {
        }

        public BerParser(BinaryReader reader)
        {
            _reader = reader;
        }

        public virtual Asn1Value ReadValue()
        {
            // Read the tag
            var header = ReadHeader();

            if (header.Class == Asn1Class.ContextSpecific)
            {
                var inner = ReadValue();
                return new Asn1ExplicitTag(
                    header.Tag,
                    inner);
            }

            Subparser subparser;
            if (_parsers.TryGetValue(header.Tag, out subparser))
            {
                return subparser(this, header);
            }

            // Unknown tag, but we do have enough to read this node and move on, so do that.
            return ParseUnknown(header);
        }

        private Asn1Oid ParseOid(BerHeader header)
        {
            byte[] octets = _reader.ReadBytes(header.Length);

            // First Octet = 40*v1+v2
            int first = octets[0] / 40;
            int second = octets[0] % 40;

            List<int> segments = new List<int>();
            segments.Add(first);
            segments.Add(second);

            // Remaining octets are encoded as base-128 digits, where the highest bit indicates if more digits exist
            int idx = 1;
            while (idx < octets.Length)
            {
                int val = 0;
                do
                {
                    val = (val * 128) + (octets[idx] & 0x7F); // Take low 7 bits of octet
                    idx++;
                } while ((octets[idx-1] & 0x80) != 0); // Loop while high bit is 1
                segments.Add(val);
            }

            return new Asn1Oid(
                header.Class,
                header.Tag,
                segments);
        }

        private Asn1Sequence ParseSequence(BerHeader header)
        {
            long start = _reader.BaseStream.Position;
            List<Asn1Value> values = new List<Asn1Value>();
            while ((_reader.BaseStream.Position - start) < header.Length)
            {
                values.Add(ReadValue());
            }
            return new Asn1Sequence(
                header.Class,
                header.Tag,
                values);
        }

        private Asn1Value ParseUnknown(BerHeader header)
        {
            // Read the contents
            var content = _reader.ReadBytes(header.Length);

            // Construct the value!
            return new Asn1Unknown(
                header.Class,
                header.Tag,
                content);
        }

        internal BerHeader ReadHeader()
        {
            byte lowTag = _reader.ReadByte();
            byte classNumber = (byte)((lowTag & 0xC0) >> 6); // Extract top 2 bits and shift down
            bool primitive = ((lowTag & 0x20) == 0);
            int tag = lowTag & 0x1F; // Extract bottom 5 bits
            if (tag == 0x1F)
            {
                tag = ReadBase128VarInt();
            }

            // Read the length
            byte lowLen = _reader.ReadByte();
            var len = lowLen & 0x7F;
            if ((lowLen & 0x80) != 0 && len != 0) // Bit 8 set and not indeterminate length?
            {
                // Len is actually the number of length octets left, each one is a base 256 "digit"
                var lengthBytes = _reader.ReadBytes(len);
                len = lengthBytes.Aggregate(
                    seed: 0,
                    func: (l, r) => (l * 256) + r);
            }
            return new BerHeader(
                (Asn1Class)classNumber,
                tag,
                len,
                primitive ?
                    Asn1Encoding.PrimativeDefiniteLength :
                    (len == 0 ?
                        Asn1Encoding.ConstructedIndefiniteLength :
                        Asn1Encoding.ConstructedDefiniteLength));
        }

        private int ReadBase128VarInt()
        {
            int val = 0;
            byte cur;
            do
            {
                cur = _reader.ReadByte();
                val = (val * 128) + (cur & 0x7F);
            } while ((cur & 0x80) != 0);
            return val;
        }
    }

    internal struct BerHeader
    {
        public Asn1Class Class { get; }
        public Asn1Encoding Encoding { get; }
        public int Tag { get; }
        public int Length { get; }

        public BerHeader(Asn1Class @class, int tag, int length, Asn1Encoding encoding) : this()
        {
            Class = @class;
            Tag = tag;
            Encoding = encoding;
            Length = length;
        }
    }
}