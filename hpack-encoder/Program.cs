// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Linq;
using System.IO;
using System.Data;
using System.Collections;
using System.Net.Http.HPack;

namespace hpack_encoder
{
    class Program
    {
        static void Main(string[] args)
        {
            GenerateSeeds();
        }

        static void GenerateSeeds()
        {
            Random rng = new Random(0);

            HPackEncoder.TableEntry[] staticIndexedHeaders = HPackEncoder.s_staticTable.Where(x => x.Value.Length != 0).ToArray();
            string[] staticIndexedNames = HPackEncoder.s_staticTable.Select(x => x.Name).Distinct().ToArray();

            int seedLen = 1024;

            for (int i = 0; i < 10; ++i)
            {
                Console.WriteLine($"generating seed {i}");

                seedLen += rng.Next(1024, 10240);

                GenerateSeed(enc =>
                {
                    enc.EncodeNewDynamicTableSize(4096);

                    while (enc.BytesWritten < seedLen)
                    {
                        string name, value;
                        HPackFlags flags = HPackFlags.None;

                        switch (rng.Next(5))
                        {
                            case 0:
                                Console.WriteLine("fully indexed, static.");
                                HPackEncoder.TableEntry e = staticIndexedHeaders[rng.Next(staticIndexedHeaders.Length)];
                                (name, value) = (e.Name, e.Value);
                                break;
                            case 1:
                                if (enc.DynamicTableCount == 0 || rng.Next(5) == 0)
                                {
                                    Console.WriteLine("new dynamic index.");
                                    name = GenerateName();
                                    value = GenerateValue();
                                    flags = HPackFlags.NewIndexed;
                                }
                                else
                                {
                                    Console.WriteLine("fully indexed, dynamic.");
                                    e = enc.DynamicTable.ElementAt(rng.Next(enc.DynamicTableCount));
                                    (name, value) = (e.Name, e.Value);
                                }
                                break;
                            case 2: // indexed name, static.
                                Console.WriteLine("indexed name, static.");
                                name = staticIndexedNames[rng.Next(staticIndexedNames.Length)];
                                value = GenerateValue();
                                break;
                            case 3: // indexed name, dynamic.
                                if (enc.DynamicTableCount == 0 || rng.Next(5) == 0)
                                {
                                    Console.WriteLine("new dynamic index.");
                                    name = GenerateName();
                                    value = GenerateValue();
                                    flags = HPackFlags.NewIndexed;
                                }
                                else
                                {
                                    Console.WriteLine("indexed name, dynamic.");
                                    e = enc.DynamicTable.ElementAt(rng.Next(enc.DynamicTableCount));
                                    name = e.Name;
                                    value = GenerateValue();
                                }
                                break;
                            case 4: // literal name.
                                Console.WriteLine("literal name.");
                                name = GenerateName();
                                value = GenerateValue();
                                break;
                            default:
                                throw new Exception("should never be reached A");
                        }

                        if (flags != HPackFlags.NewIndexed)
                        {
                            switch (rng.Next(3))
                            {
                                case 0:
                                    flags |= HPackFlags.WithoutIndexing;
                                    break;
                                case 1:
                                    flags |= HPackFlags.NewIndexed;
                                    break;
                                case 2:
                                    flags |= HPackFlags.NeverIndexed;
                                    break;
                                default:
                                    throw new Exception("should never be reached B");
                            }
                        }

                        if (rng.Next(2) == 0) flags |= HPackFlags.HuffmanEncodeName;
                        if (rng.Next(2) == 0) flags |= HPackFlags.HuffmanEncodeValue;

                        enc.Encode(name, value, flags);
                    }
                });
            }

            string GenerateName() => GenerateString(rng.Next(4, 16));
            string GenerateValue() => GenerateString(rng.Next(4, 64));

            string GenerateString(int len)
            {
                char[] buffer = new char[len];

                for (int i = 0; i < len; ++i)
                {
                    buffer[i] = (char)('a' + rng.Next(0, 26));
                }

                return new string(buffer);
            }
        }

        delegate void EncodeSeedDelegate(HPackEncoder enc);

        static int seedNo = 0;

        static void GenerateSeed(EncodeSeedDelegate encodeFunc)
        {
            byte[] buffer = new byte[1024*1024];
            var encoder = new HPackEncoder(buffer, 1024);

            encodeFunc(encoder);
            encoder.VerifyComplete();

            File.WriteAllBytes($"seed_{++seedNo}.bin", buffer.AsSpan(0, encoder.BytesWritten).ToArray());
        }

        static void RunTests()
        {
            Test("literal header field with indexing",
                new byte[] { 0x40, 0x0a, 0x63, 0x75, 0x73, 0x74, 0x6f, 0x6d, 0x2d, 0x6b, 0x65, 0x79, 0x0d, 0x63, 0x75, 0x73, 0x74, 0x6f, 0x6d, 0x2d, 0x68, 0x65, 0x61, 0x64, 0x65, 0x72 },
                buffer =>
                {
                    int bytes = HPackEncoder.EncodeHeader("custom-key", "custom-header", HPackFlags.NewIndexed, buffer.Span);
                    return bytes;
                });

            Test("literal header field without indexing",
                new byte[] { 0x04, 0x0c, 0x2f, 0x73, 0x61, 0x6d, 0x70, 0x6c, 0x65, 0x2f, 0x70, 0x61, 0x74, 0x68 },
                buffer =>
                {
                    int bytes = HPackEncoder.EncodeHeader(HPackEncoder.GetStaticIndex(":path"), "/sample/path", HPackFlags.WithoutIndexing, buffer.Span);
                    return bytes;
                });

            Test("literal header never indexed",
                new byte[] { 0x10, 0x08, 0x70, 0x61, 0x73, 0x73, 0x77, 0x6f, 0x72, 0x64, 0x06, 0x73, 0x65, 0x63, 0x72, 0x65, 0x74 },
                buffer =>
                {
                    int bytes = HPackEncoder.EncodeHeader("password", "secret", HPackFlags.NeverIndexed, buffer.Span);
                    return bytes;
                });

            Test("indexed header field using dynamic table",
                new byte[]
                {
                    // Newly indexed literal header.
                    0x40, 0x0a, 0x63, 0x75, 0x73, 0x74, 0x6f, 0x6d, 0x2d, 0x6b, 0x65, 0x79, 0x0d, 0x63, 0x75, 0x73, 0x74, 0x6f, 0x6d, 0x2d, 0x68, 0x65, 0x61, 0x64, 0x65, 0x72,
                    // Indexed header using dynamic table for full name/value.
                    0xBE,
                    // Header using dynamic table for just name, with a literal value.
                    0x0F, 0x2F, 0x0A, 0x6E, 0x65, 0x77, 0x2D, 0x68, 0x65, 0x61, 0x64, 0x65, 0x72
                },
                buffer =>
                {
                    return new HPackEncoder(buffer)
                        .Encode("custom-key", "custom-header", HPackFlags.NewIndexed)
                        .Encode("custom-key", "custom-header", HPackFlags.WithoutIndexing)
                        .Encode("custom-key", "new-header", HPackFlags.None)
                        .VerifyComplete()
                        .BytesWritten;
                });
        }

        delegate int EncodeTestDelegate(Memory<byte> buffer);

        static void Test(string name, byte[] expectedValue, EncodeTestDelegate doEncode)
        {
            Memory<byte> buffer = new byte[4096];
            int len = doEncode(buffer);
            buffer = buffer.Slice(0, len);

            string success = buffer.Span.SequenceEqual(expectedValue) ? "success" : "fail";

            Console.WriteLine($"{name}: {success}");
            Console.WriteLine($"  got:      {ToHex(buffer.Span)}");
            Console.WriteLine($"  expected: {ToHex(expectedValue)}");
            Console.WriteLine();
        }

        static string ToHex(ReadOnlySpan<byte> span)
        {
            StringBuilder sb = new StringBuilder(span.Length * 2);
            for (int i = 0; i < span.Length; ++i)
            {
                sb.AppendFormat(span[i].ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
