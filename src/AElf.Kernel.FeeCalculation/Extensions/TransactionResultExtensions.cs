using System.Collections.Generic;
using System.Linq;
using AElf.Contracts.MultiToken;
using AElf.CSharp.Core;
using AElf.Types;

namespace AElf.Kernel.FeeCalculation.Extensions;

public static class TransactionResultExtensions
{
    public static Dictionary<string, long> GetChargedTransactionFees(this TransactionResult transactionResult)
    {
        return transactionResult.Logs
            .Where(l => l.Name == nameof(TransactionFeeCharged))
            .Select(l => TransactionFeeCharged.Parser.ParseFrom(l.NonIndexed))
            .GroupBy(fee => fee.Symbol, fee => fee.Amount)
            .ToDictionary(g => g.Key, g => g.Sum());
    }

    public static Dictionary<string, long> GetConsumedResourceTokens(this TransactionResult transactionResult)
    {
        var relatedLogs = transactionResult.Logs.Where(l => l.Name == nameof(ResourceTokenCharged)).ToList();
        if (!relatedLogs.Any()) return new Dictionary<string, long>();
        return relatedLogs.Select(l => ResourceTokenCharged.Parser.ParseFrom(l.NonIndexed))
            .ToDictionary(e => e.Symbol, e => e.Amount);
    }

    public static Dictionary<string, long> GetOwningResourceTokens(this TransactionResult transactionResult)
    {
        var relatedLogs = transactionResult.Logs.Where(l => l.Name == nameof(ResourceTokenOwned)).ToList();
        if (!relatedLogs.Any()) return new Dictionary<string, long>();
        return relatedLogs.Select(l => ResourceTokenOwned.Parser.ParseFrom(l.NonIndexed))
            .ToDictionary(e => e.Symbol, e => e.Amount);
    }
}