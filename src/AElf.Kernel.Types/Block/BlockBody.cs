﻿using System;
using AElf.Types;
using Google.Protobuf;

namespace AElf.Kernel;

public partial class BlockBody : IBlockBody
{
    private Hash _blockBodyHash;
    public int TransactionsCount => TransactionIds.Count;

    public Hash GetHash()
    {
        return _blockBodyHash ?? CalculateBodyHash();
    }

    private Hash CalculateBodyHash()
    {
        if (!VerifyFields())
            throw new InvalidOperationException("Invalid block body.");

        _blockBodyHash = HashHelper.ComputeFrom(this.ToByteArray());
        return _blockBodyHash;
    }

    public bool VerifyFields()
    {
        if (TransactionIds.Count == 0)
            return false;

        return true;
    }
}