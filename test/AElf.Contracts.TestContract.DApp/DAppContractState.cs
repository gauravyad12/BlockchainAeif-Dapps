﻿using AElf.Contracts.MultiToken;
using AElf.Contracts.TokenHolder;
using AElf.Sdk.CSharp.State;
using AElf.Standards.ACS1;
using AElf.Standards.ACS10;
using AElf.Standards.ACS9;
using AElf.Types;

namespace AElf.Contracts.TestContract.DApp;

public class DAppContractState : ContractState
{
    internal TokenHolderContractContainer.TokenHolderContractReferenceState TokenHolderContract { get; set; }
    internal TokenContractImplContainer.TokenContractImplReferenceState TokenContract { get; set; }
    internal DividendPoolContractContainer.DividendPoolContractReferenceState DividendPoolContract { get; set; }

    public SingletonState<string> Symbol { get; set; }
    public SingletonState<Address> ProfitReceiver { get; set; }
    public MappedState<Address, Profile> Profiles { get; set; }

    public MappedState<string, MethodFees> TransactionFees { get; set; }

    public SingletonState<ProfitConfig> ProfitConfig { get; set; }
}