﻿using System.Linq;
using AElf.Contracts.MultiToken;
using AElf.CSharp.Core;
using AElf.Sdk.CSharp;
using AElf.Types;
using Google.Protobuf.WellKnownTypes;

namespace AElf.Contracts.Vote;

/// <summary>
///     Comments and documents see README.md of current project.
/// </summary>
public partial class VoteContract : VoteContractImplContainer.VoteContractImplBase
{
    /// <summary>
    ///     To register a new voting item while filling up with details.
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public override Empty Register(VotingRegisterInput input)
    {
        var votingItemId = AssertValidNewVotingItem(input);

        if (State.TokenContract.Value == null)
            State.TokenContract.Value =
                Context.GetContractAddressByName(SmartContractConstants.TokenContractSystemName);

        // Accepted currency is in white list means this token symbol supports voting.
        var isInWhiteList = State.TokenContract.IsInWhiteList.Call(new IsInWhiteListInput
        {
            Symbol = input.AcceptedCurrency,
            Address = Context.Self
        }).Value;
        Assert(isInWhiteList, "Claimed accepted token is not available for voting.");

        // Initialize voting event.
        var votingItem = new VotingItem
        {
            Sponsor = Context.Sender,
            VotingItemId = votingItemId,
            AcceptedCurrency = input.AcceptedCurrency,
            IsLockToken = input.IsLockToken,
            TotalSnapshotNumber = input.TotalSnapshotNumber,
            CurrentSnapshotNumber = 1,
            CurrentSnapshotStartTimestamp = input.StartTimestamp,
            StartTimestamp = input.StartTimestamp,
            EndTimestamp = input.EndTimestamp,
            RegisterTimestamp = Context.CurrentBlockTime,
            Options = { input.Options },
            IsQuadratic = input.IsQuadratic,
            TicketCost = input.TicketCost
        };

        State.VotingItems[votingItemId] = votingItem;

        // Initialize first voting going information of registered voting event.
        var votingResultHash = GetVotingResultHash(votingItemId, 1);
        State.VotingResults[votingResultHash] = new VotingResult
        {
            VotingItemId = votingItemId,
            SnapshotNumber = 1,
            SnapshotStartTimestamp = input.StartTimestamp
        };

        Context.Fire(new VotingItemRegistered
        {
            Sponsor = votingItem.Sponsor,
            VotingItemId = votingItemId,
            AcceptedCurrency = votingItem.AcceptedCurrency,
            IsLockToken = votingItem.IsLockToken,
            TotalSnapshotNumber = votingItem.TotalSnapshotNumber,
            CurrentSnapshotNumber = votingItem.CurrentSnapshotNumber,
            CurrentSnapshotStartTimestamp = votingItem.StartTimestamp,
            StartTimestamp = votingItem.StartTimestamp,
            EndTimestamp = votingItem.EndTimestamp,
            RegisterTimestamp = votingItem.RegisterTimestamp,
            IsQuadratic = votingItem.IsQuadratic,
            TicketCost = votingItem.TicketCost
        });

        return new Empty();
    }

    /// <summary>
    ///     Execute the Vote action,save the VoteRecords and update the VotingResults and the VotedItems
    ///     Before Voting,the VotingItem's token must be locked,except the votes delegated to a contract.
    /// </summary>
    /// <param name="input">VoteInput</param>
    /// <returns></returns>
    public override Empty Vote(VoteInput input)
    {
        var votingItem = AssertValidVoteInput(input);
        var amount = 0L;
        if (!votingItem.IsQuadratic)
        {
            amount = input.Amount;
        }
        else
        {
            var currentVotesCount = State.QuadraticVotesCountMap[input.VoteId].Add(1);
            State.QuadraticVotesCountMap[input.VoteId] = currentVotesCount;
            amount = votingItem.TicketCost.Mul(currentVotesCount);
        }

        var votingRecord = new VotingRecord
        {
            VotingItemId = input.VotingItemId,
            Amount = amount,
            SnapshotNumber = votingItem.CurrentSnapshotNumber,
            Option = input.Option,
            IsWithdrawn = false,
            VoteTimestamp = Context.CurrentBlockTime,
            Voter = input.Voter,
            IsChangeTarget = input.IsChangeTarget
        };

        State.VotingRecords[input.VoteId] = votingRecord;

        UpdateVotingResult(votingItem, input.Option, votingItem.IsQuadratic ? 1 : amount);
        UpdateVotedItems(input.VoteId, votingRecord.Voter, votingItem);

        if (votingItem.IsLockToken)
            // Lock voted token.
            State.TokenContract.Lock.Send(new LockInput
            {
                Address = votingRecord.Voter,
                Symbol = votingItem.AcceptedCurrency,
                LockId = input.VoteId,
                Amount = amount
            });

        Context.Fire(new Voted
        {
            VoteId = input.VoteId,
            VotingItemId = votingRecord.VotingItemId,
            Voter = votingRecord.Voter,
            Amount = votingRecord.Amount,
            Option = votingRecord.Option,
            SnapshotNumber = votingRecord.SnapshotNumber,
            VoteTimestamp = votingRecord.VoteTimestamp
        });

        return new Empty();
    }

    private void UpdateVotedItems(Hash voteId, Address voter, VotingItem votingItem)
    {
        var votedItems = State.VotedItemsMap[voter] ?? new VotedItems();
        var voterItemIndex = votingItem.VotingItemId.ToHex();
        if (votedItems.VotedItemVoteIds.ContainsKey(voterItemIndex))
            votedItems.VotedItemVoteIds[voterItemIndex].ActiveVotes.Add(voteId);
        else
            votedItems.VotedItemVoteIds[voterItemIndex] =
                new VotedIds
                {
                    ActiveVotes = { voteId }
                };

        votedItems.VotedItemVoteIds[voterItemIndex].WithdrawnVotes.Remove(voteId);
        State.VotedItemsMap[voter] = votedItems;
    }

    /// <summary>
    ///     Update the State.VotingResults.include the VotersCount,VotesAmount and the votes int the results[option]
    /// </summary>
    /// <param name="votingItem"></param>
    /// <param name="option"></param>
    /// <param name="amount"></param>
    private void UpdateVotingResult(VotingItem votingItem, string option, long amount)
    {
        // Update VotingResult based on this voting behaviour.
        var votingResultHash = GetVotingResultHash(votingItem.VotingItemId, votingItem.CurrentSnapshotNumber);
        var votingResult = State.VotingResults[votingResultHash];
        if (!votingResult.Results.ContainsKey(option)) votingResult.Results.Add(option, 0);

        var currentVotes = votingResult.Results[option];
        votingResult.Results[option] = currentVotes.Add(amount);
        votingResult.VotersCount = votingResult.VotersCount.Add(1);
        votingResult.VotesAmount = votingResult.VotesAmount.Add(amount);
        State.VotingResults[votingResultHash] = votingResult;
    }

    /// <summary>
    ///     Withdraw the Votes.
    ///     first,mark the related record IsWithdrawn.
    ///     second,delete the vote form ActiveVotes and add the vote to withdrawnVotes.
    ///     finally,unlock the token that Locked in the VotingItem
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public override Empty Withdraw(WithdrawInput input)
    {
        var votingRecord = State.VotingRecords[input.VoteId];
        if (votingRecord == null) throw new AssertionException("Voting record not found.");
        var votingItem = State.VotingItems[votingRecord.VotingItemId];

        if (votingItem.IsLockToken)
            Assert(votingRecord.Voter == Context.Sender, "No permission to withdraw votes of others.");
        else
            Assert(votingItem.Sponsor == Context.Sender, "No permission to withdraw votes of others.");

        // Update VotingRecord.
        votingRecord.IsWithdrawn = true;
        votingRecord.WithdrawTimestamp = Context.CurrentBlockTime;
        State.VotingRecords[input.VoteId] = votingRecord;

        var votingResultHash = GetVotingResultHash(votingRecord.VotingItemId, votingRecord.SnapshotNumber);

        var votedItems = State.VotedItemsMap[votingRecord.Voter];
        votedItems.VotedItemVoteIds[votingItem.VotingItemId.ToHex()].ActiveVotes.Remove(input.VoteId);
        votedItems.VotedItemVoteIds[votingItem.VotingItemId.ToHex()].WithdrawnVotes.Add(input.VoteId);
        State.VotedItemsMap[votingRecord.Voter] = votedItems;

        var votingResult = State.VotingResults[votingResultHash];
        votingResult.Results[votingRecord.Option] =
            votingResult.Results[votingRecord.Option].Sub(votingRecord.Amount);
        if (!votedItems.VotedItemVoteIds[votingRecord.VotingItemId.ToHex()].ActiveVotes.Any())
            votingResult.VotersCount = votingResult.VotersCount.Sub(1);

        votingResult.VotesAmount = votingResult.VotesAmount.Sub(votingRecord.Amount);

        State.VotingResults[votingResultHash] = votingResult;

        if (votingItem.IsLockToken)
            State.TokenContract.Unlock.Send(new UnlockInput
            {
                Address = votingRecord.Voter,
                Symbol = votingItem.AcceptedCurrency,
                Amount = votingRecord.Amount,
                LockId = input.VoteId
            });

        Context.Fire(new Withdrawn
        {
            VoteId = input.VoteId
        });

        return new Empty();
    }

    public override Empty TakeSnapshot(TakeSnapshotInput input)
    {
        var votingItem = AssertVotingItem(input.VotingItemId);

        Assert(votingItem.Sponsor == Context.Sender, "Only sponsor can take snapshot.");

        Assert(votingItem.CurrentSnapshotNumber - 1 < votingItem.TotalSnapshotNumber,
            "Current voting item already ended.");

        // Update previous voting going information.
        var previousVotingResultHash = GetVotingResultHash(input.VotingItemId, votingItem.CurrentSnapshotNumber);
        var previousVotingResult = State.VotingResults[previousVotingResultHash];
        previousVotingResult.SnapshotEndTimestamp = Context.CurrentBlockTime;
        State.VotingResults[previousVotingResultHash] = previousVotingResult;

        Assert(votingItem.CurrentSnapshotNumber == input.SnapshotNumber,
            $"Can only take snapshot of current snapshot number: {votingItem.CurrentSnapshotNumber}, but {input.SnapshotNumber}");
        var nextSnapshotNumber = input.SnapshotNumber.Add(1);
        votingItem.CurrentSnapshotNumber = nextSnapshotNumber;
        State.VotingItems[votingItem.VotingItemId] = votingItem;

        // Initial next voting going information.
        var currentVotingGoingHash = GetVotingResultHash(input.VotingItemId, nextSnapshotNumber);
        State.VotingResults[currentVotingGoingHash] = new VotingResult
        {
            VotingItemId = input.VotingItemId,
            SnapshotNumber = nextSnapshotNumber,
            SnapshotStartTimestamp = Context.CurrentBlockTime,
            VotersCount = previousVotingResult.VotersCount,
            VotesAmount = previousVotingResult.VotesAmount
        };
        return new Empty();
    }

    /// <summary>
    ///     Add a option for corresponding VotingItem.
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public override Empty AddOption(AddOptionInput input)
    {
        var votingItem = AssertVotingItem(input.VotingItemId);
        Assert(votingItem.Sponsor == Context.Sender, "Only sponsor can update options.");
        AssertOption(votingItem, input.Option);
        Assert(votingItem.Options.Count < VoteContractConstants.MaximumOptionsCount,
            $"The count of options can't greater than {VoteContractConstants.MaximumOptionsCount}");
        votingItem.Options.Add(input.Option);
        State.VotingItems[votingItem.VotingItemId] = votingItem;
        return new Empty();
    }

    private void AssertOption(VotingItem votingItem, string option)
    {
        Assert(option.Length <= VoteContractConstants.OptionLengthLimit, "Invalid input.");
        Assert(!votingItem.Options.Contains(option), "Option already exists.");
    }

    /// <summary>
    ///     Delete a option for corresponding VotingItem
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public override Empty RemoveOption(RemoveOptionInput input)
    {
        var votingItem = AssertVotingItem(input.VotingItemId);
        Assert(votingItem.Sponsor == Context.Sender, "Only sponsor can update options.");
        Assert(input.Option.Length <= VoteContractConstants.OptionLengthLimit, "Invalid input.");
        Assert(votingItem.Options.Contains(input.Option), "Option doesn't exist.");
        votingItem.Options.Remove(input.Option);
        State.VotingItems[votingItem.VotingItemId] = votingItem;
        return new Empty();
    }

    public override Empty AddOptions(AddOptionsInput input)
    {
        var votingItem = AssertVotingItem(input.VotingItemId);
        Assert(votingItem.Sponsor == Context.Sender, "Only sponsor can update options.");
        foreach (var option in input.Options) AssertOption(votingItem, option);
        votingItem.Options.AddRange(input.Options);
        Assert(votingItem.Options.Count <= VoteContractConstants.MaximumOptionsCount,
            $"The count of options can't greater than {VoteContractConstants.MaximumOptionsCount}");
        State.VotingItems[votingItem.VotingItemId] = votingItem;
        return new Empty();
    }

    public override Empty RemoveOptions(RemoveOptionsInput input)
    {
        var votingItem = AssertVotingItem(input.VotingItemId);
        Assert(votingItem.Sponsor == Context.Sender, "Only sponsor can update options.");
        foreach (var option in input.Options)
        {
            Assert(votingItem.Options.Contains(option), "Option doesn't exist.");
            Assert(option.Length <= VoteContractConstants.OptionLengthLimit, "Invalid input.");
            votingItem.Options.Remove(option);
        }

        State.VotingItems[votingItem.VotingItemId] = votingItem;
        return new Empty();
    }

    private VotingItem AssertVotingItem(Hash votingItemId)
    {
        var votingItem = State.VotingItems[votingItemId];
        Assert(votingItem != null, $"Voting item not found. {votingItemId.ToHex()}");
        return votingItem;
    }

    /// <summary>
    ///     Initialize the related contracts=>TokenContract;
    /// </summary>
    private Hash AssertValidNewVotingItem(VotingRegisterInput input)
    {
        // Use input without options and sender's address to calculate voting item id.
        var votingItemId = input.GetHash(Context.Sender);

        Assert(State.VotingItems[votingItemId] == null, "Voting item already exists.");

        // total snapshot number can't be 0. At least one epoch is required.
        if (input.TotalSnapshotNumber == 0) input.TotalSnapshotNumber = 1;

        Assert(input.EndTimestamp > input.StartTimestamp, "Invalid active time.");

        Context.LogDebug(() => $"Voting item created by {Context.Sender}: {votingItemId.ToHex()}");

        return votingItemId;
    }

    private Hash GetVotingResultHash(Hash votingItemId, long snapshotNumber)
    {
        return new VotingResult
        {
            VotingItemId = votingItemId,
            SnapshotNumber = snapshotNumber
        }.GetHash();
    }

    private VotingItem AssertValidVoteInput(VoteInput input)
    {
        var votingItem = AssertVotingItem(input.VotingItemId);
        Assert(input.Option.Length <= VoteContractConstants.OptionLengthLimit, "Invalid input.");
        Assert(votingItem.Options.Contains(input.Option), $"Option {input.Option} not found.");
        Assert(votingItem.CurrentSnapshotNumber <= votingItem.TotalSnapshotNumber,
            "Current voting item already ended.");
        if (!votingItem.IsLockToken)
        {
            Assert(votingItem.Sponsor == Context.Sender, "Sender of delegated voting event must be the Sponsor.");
            Assert(input.Voter != null, "Voter cannot be null if voting event is delegated.");
            Assert(input.VoteId != null, "Vote Id cannot be null if voting event is delegated.");
        }
        else
        {
            var votingResultHash = GetVotingResultHash(votingItem.VotingItemId, votingItem.CurrentSnapshotNumber);
            var votingResult = State.VotingResults[votingResultHash];
            // Voter = Transaction Sender
            input.Voter = Context.Sender;
            // VoteId = Transaction Id;
            input.VoteId = Context.GenerateId(Context.Self, votingResult.VotesAmount.ToBytes(false));
        }

        return votingItem;
    }
}