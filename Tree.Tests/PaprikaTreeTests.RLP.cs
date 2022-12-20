﻿using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace Tree.Tests;

public class PaprikaTreeTestsRlp
{
    [Test]
    public void Leaf_Short_To_RLP()
    {
        var key = NibblePath.FromKey(stackalloc byte[] { 0x12, 0x34 });
        Span<byte> value = stackalloc byte[] { 3, 5, 7, 11 };
        var expected = new byte[] { 201, 131, 32, 18, 52, 132, 3, 5, 7, 11 };
        
        AssertLeaf(expected, key, value);
    }
    
    [Test]
    public void Leaf_Long_To_Keccak()
    {
        var key = NibblePath.FromKey(stackalloc byte[] { 0x12, 0x34 });
        var value = new byte [32];

        var keccak = ParseHex("0xc9a263dc573d67a8d0627756d012385a27db78bb4a072ab0f755a84d3b4babda");
        
        AssertLeaf(keccak, key, value);
    }
    
    [Test]
    public void Extension_Short_To_RLP()
    {
        // var key = NibblePath.FromKey(stackalloc byte[] { 0x12, 0x34 });
        // var expected = new byte[] { 201, 131, 32, 18, 52, 132, 3, 5, 7, 11 };
        // var childEncoded = new byte[] { 1 };
        // AssertExtension(expected, key, value);
    }

    private static void AssertLeaf(byte[] expected, in NibblePath path, in ReadOnlySpan<byte> value)
    {
        Span<byte> destination = stackalloc byte[32];
        var encoded = PaprikaTree.EncodeLeaf(path, value, destination);
        AssertEncoded(expected, encoded, destination);
    }
    
    private static void AssertExtension(byte[] expected, in NibblePath path, in ReadOnlySpan<byte> childRlpOrKeccak)
    {
        Span<byte> destination = stackalloc byte[32];
        var encoded = PaprikaTree.EncodeExtension(path, childRlpOrKeccak, destination);
        AssertEncoded(expected, encoded, destination);
    }

    private static void AssertEncoded(byte[] expected, byte encoded, Span<byte> destination)
    {
        if (encoded == PaprikaTree.HasKeccak)
        {
            // keccak
            CollectionAssert.AreEqual(expected, destination.ToArray());
        }
        else
        {
            // rlp
            var length = destination[0];
            var rlp = destination.Slice(1, length);
            CollectionAssert.AreEqual(expected, rlp.ToArray());
        }
    }


    private static byte[] ParseHex(string hex)
    {
        hex = hex.Replace("0x", "");
        var result = new byte[hex.Length / 2];
        
        for (int i = 0; i < hex.Length; i+=2)
        {
            result[i / 2] = byte.Parse(hex.Substring(i, 2), NumberStyles.HexNumber);
        }

        return result;
    }
}