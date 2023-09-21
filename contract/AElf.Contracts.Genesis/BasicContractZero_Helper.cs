using System;
using AElf.Contracts.Parliament;
using AElf.CSharp.Core.Extension;
using AElf.Sdk.CSharp;
using AElf.Standards.ACS0;
using AElf.Standards.ACS3;
using AElf.Types;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace AElf.Contracts.Genesis;

public partial class BasicContractZero
{
    private void AddCodeHashToList(Hash codeHash)
    {
        var contractCodeHashList =
            State.ContractCodeHashListMap[Context.CurrentHeight] ?? new ContractCodeHashList();
        contractCodeHashList.Value.Add(codeHash);
        State.ContractCodeHashListMap[Context.CurrentHeight] = contractCodeHashList;
    }

    private ContractDeployed DeploySmartContractInternalCore(SmartContractRegistration reg, Address author,
        Address contractAddress, long serialNumber, Hash name = null)
    {
        AssertContractNotExists(reg.CodeHash);

        Assert(State.ContractInfos[contractAddress] == null, "Contract address exists.");
        var contractInfo = Context.DeploySmartContract(contractAddress, reg, name);

        State.ContractInfos[contractAddress] = Extensions.CreateContractInfo(reg, author)
            .SetSerialNumber(serialNumber)
            .SetContractVersion(contractInfo.ContractVersion);
        State.SmartContractRegistrations[reg.CodeHash] = reg.SetContractVersion(contractInfo.ContractVersion);
        AddCodeHashToList(reg.CodeHash);

        Context.LogDebug(() => "BasicContractZero - Deployment ContractHash: " + reg.CodeHash.ToHex());
        Context.LogDebug(() => "BasicContractZero - Deployment success: " + contractAddress.ToBase58());
        return Extensions.CreateContractDeployedEvent(reg, contractAddress).SetAuthor(author).SetName(name);
    }

    private Address DeploySmartContractInternal(SmartContractRegistration reg, Address author, Hash name = null)
    {
        var serialNumber = State.ContractSerialNumber.Value;
        // Increment
        State.ContractSerialNumber.Value = serialNumber + 1;
        var contractAddress = AddressHelper.ComputeContractAddress(Context.ChainId, serialNumber);

        Context.Fire(DeploySmartContractInternalCore(reg, author, contractAddress, serialNumber, name));

        return contractAddress;
    }

    private Address DeploySmartContractInternal(SmartContractRegistration reg, Address author, Address deployer,
        Hash salt)
    {
        var contractAddress = AddressHelper.ComputeContractAddress(deployer, salt);
        Context.Fire(DeploySmartContractInternalCore(reg, author, contractAddress, 0, null));

        return contractAddress;
    }

    private void UpdateSmartContract(Address contractAddress, byte[] code, Address author, bool isUserContract)
    {
        var info = State.ContractInfos[contractAddress];
        Assert(info != null, "Contract not found.");
        Assert(author == info.Author, "No permission.");

        var oldCodeHash = info.CodeHash;
        var newCodeHash = HashHelper.ComputeFrom(code);
        Assert(oldCodeHash != newCodeHash, "Code is not changed.");
        AssertContractNotExists(newCodeHash);

        info.CodeHash = newCodeHash;
        info.IsUserContract = isUserContract;
        info.Version++;

        var reg = new SmartContractRegistration
        {
            Category = info.Category,
            Code = ByteString.CopyFrom(code),
            CodeHash = newCodeHash,
            IsSystemContract = info.IsSystemContract,
            Version = info.Version,
            ContractAddress = contractAddress,
            IsUserContract = isUserContract
        };

        var contractInfo = Context.UpdateSmartContract(contractAddress, reg, null, info.ContractVersion);
        Assert(contractInfo.IsSubsequentVersion,
            $"The version to be deployed is lower than the effective version({info.ContractVersion}), please correct the version number.");

        info.ContractVersion = contractInfo.ContractVersion;
        reg.ContractVersion = info.ContractVersion;

        State.ContractInfos[contractAddress] = info;
        State.SmartContractRegistrations[reg.CodeHash] = reg;

        Context.Fire(new CodeUpdated
        {
            Address = contractAddress,
            OldCodeHash = oldCodeHash,
            NewCodeHash = newCodeHash,
            Version = info.Version,
            ContractVersion = info.ContractVersion
        });

        Context.LogDebug(() => "BasicContractZero - update success: " + contractAddress.ToBase58());
    }

    private void RequireSenderAuthority(Address address = null)
    {
        if (!State.Initialized.Value)
        {
            // only authority of contract zero is valid before initialization
            AssertSenderAddressWith(Context.Self);
            return;
        }

        var isGenesisOwnerAuthorityRequired = State.ContractDeploymentAuthorityRequired.Value;
        if (!isGenesisOwnerAuthorityRequired)
            return;

        if (address != null)
            AssertSenderAddressWith(address);
    }

    private void RequireParliamentContractAddressSet()
    {
        if (State.ParliamentContract.Value == null)
            State.ParliamentContract.Value =
                Context.GetContractAddressByName(SmartContractConstants.ParliamentContractSystemName);
    }

    private void AssertSenderAddressWith(Address address)
    {
        Assert(Context.Sender == address, "Unauthorized behavior.");
    }

    private Hash CalculateHashFromInput(IMessage input)
    {
        return HashHelper.ComputeFrom(input);
    }

    private bool CheckOrganizationExist(AuthorityInfo authorityInfo)
    {
        return Context.Call<BoolValue>(authorityInfo.ContractAddress,
            nameof(AuthorizationContractContainer.AuthorizationContractReferenceState.ValidateOrganizationExist),
            authorityInfo.OwnerAddress).Value;
    }

    private bool TryClearContractProposingData(Hash inputHash, out ContractProposingInput contractProposingInput)
    {
        contractProposingInput = State.ContractProposingInputMap[inputHash];
        var isGenesisOwnerAuthorityRequired = State.ContractDeploymentAuthorityRequired.Value;
        if (isGenesisOwnerAuthorityRequired)
            Assert(
                contractProposingInput != null, "Contract proposing data not found.");

        if (contractProposingInput == null)
            return false;

        Assert(contractProposingInput.Status == ContractProposingInputStatus.CodeChecked,
            "Invalid contract proposing status.");
        State.ContractProposingInputMap.Remove(inputHash);
        return true;
    }

    private void RegisterContractProposingData(Hash proposedContractInputHash)
    {
        var registered = State.ContractProposingInputMap[proposedContractInputHash];
        Assert(registered == null || Context.CurrentBlockTime >= registered.ExpiredTime, "Already proposed.");
        var expirationTimePeriod = GetCurrentContractProposalExpirationTimePeriod();
        State.ContractProposingInputMap[proposedContractInputHash] = new ContractProposingInput
        {
            Proposer = Context.Sender,
            Status = ContractProposingInputStatus.Proposed,
            ExpiredTime = Context.CurrentBlockTime.AddSeconds(expirationTimePeriod)
        };
    }

    private void CreateParliamentOrganizationForInitialControllerAddress(bool proposerAuthorityRequired)
    {
        RequireParliamentContractAddressSet();
        var parliamentProposerWhitelist = State.ParliamentContract.GetProposerWhiteList.Call(new Empty());

        var isWhiteListEmpty = parliamentProposerWhitelist.Proposers.Count == 0;
        State.ParliamentContract.CreateOrganizationBySystemContract.Send(new CreateOrganizationBySystemContractInput
        {
            OrganizationCreationInput = new CreateOrganizationInput
            {
                ProposalReleaseThreshold = new ProposalReleaseThreshold
                {
                    MinimalApprovalThreshold = MinimalApprovalThreshold,
                    MinimalVoteThreshold = MinimalVoteThresholdThreshold,
                    MaximalRejectionThreshold = MaximalRejectionThreshold,
                    MaximalAbstentionThreshold = MaximalAbstentionThreshold
                },
                ProposerAuthorityRequired = proposerAuthorityRequired,
                ParliamentMemberProposingAllowed = isWhiteListEmpty
            },
            OrganizationAddressFeedbackMethod = nameof(SetInitialControllerAddress)
        });
    }

    private void AssertAuthorityByContractInfo(ContractInfo contractInfo, Address address)
    {
        Assert(contractInfo.Author == Context.Self || address == contractInfo.Author, "No permission.");
    }

    private bool ValidateProposerAuthority(Address contractAddress, Address organizationAddress, Address proposer)
    {
        return Context.Call<BoolValue>(contractAddress,
            nameof(AuthorizationContractContainer.AuthorizationContractReferenceState.ValidateProposerInWhiteList),
            new ValidateProposerInWhiteListInput
            {
                OrganizationAddress = organizationAddress,
                Proposer = proposer
            }).Value;
    }

    private Address DecideNonSystemContractAuthor(Address proposer, Address sender)
    {
        if (!State.ContractDeploymentAuthorityRequired.Value)
            return sender;

        var contractDeploymentController = State.ContractDeploymentController.Value;
        var isProposerInWhiteList = ValidateProposerAuthority(contractDeploymentController.ContractAddress,
            contractDeploymentController.OwnerAddress, proposer);
        return isProposerInWhiteList ? proposer : Context.Self;
    }

    private ByteString ExtractCodeFromContractCodeCheckInput(ContractCodeCheckInput input)
    {
        return input.CodeCheckReleaseMethod == nameof(DeploySmartContract)
            ? ContractDeploymentInput.Parser.ParseFrom(input.ContractInput).Code
            : ContractUpdateInput.Parser.ParseFrom(input.ContractInput).Code;
    }

    private void AssertCodeCheckProposingInput(ContractCodeCheckInput input)
    {
        Assert(
            input.CodeCheckReleaseMethod == nameof(DeploySmartContract) ||
            input.CodeCheckReleaseMethod == nameof(UpdateSmartContract), "Invalid input.");
    }

    private int GetCurrentContractProposalExpirationTimePeriod()
    {
        return State.ContractProposalExpirationTimePeriod.Value == 0
            ? ContractProposalExpirationTimePeriod
            : State.ContractProposalExpirationTimePeriod.Value;
    }

    private void AssertCurrentMiner()
    {
        RequireConsensusContractStateSet();
        var isCurrentMiner = State.ConsensusContract.IsCurrentMiner.Call(Context.Sender).Value;
        Context.LogDebug(() => $"Sender is currentMiner : {isCurrentMiner}.");
        Assert(isCurrentMiner, "No permission.");
    }

    private void RequireConsensusContractStateSet()
    {
        if (State.ConsensusContract.Value != null)
            return;
        State.ConsensusContract.Value =
            Context.GetContractAddressByName(SmartContractConstants.ConsensusContractSystemName);
    }

    private void SendUserContractProposal(Hash proposingInputHash, string releaseMethodName, ByteString @params)
    {
        var registered = State.ContractProposingInputMap[proposingInputHash];
        Assert(registered == null || Context.CurrentBlockTime >= registered.ExpiredTime, "Already proposed.");
        var proposedInfo = new ContractProposingInput
        {
            Proposer = Context.Self,
            Status = ContractProposingInputStatus.CodeCheckProposed,
            ExpiredTime = Context.CurrentBlockTime.AddSeconds(CodeCheckProposalExpirationTimePeriod),
            Author = Context.Sender
        };
        State.ContractProposingInputMap[proposingInputHash] = proposedInfo;

        var codeCheckController = State.CodeCheckController.Value;
        var proposalCreationInput = new CreateProposalBySystemContractInput
        {
            ProposalInput = new CreateProposalInput
            {
                ToAddress = Context.Self,
                ContractMethodName = releaseMethodName,
                Params = @params,
                OrganizationAddress = codeCheckController.OwnerAddress,
                ExpiredTime = proposedInfo.ExpiredTime
            },
            OriginProposer = Context.Self
        };

        Context.SendInline(codeCheckController.ContractAddress,
            nameof(AuthorizationContractContainer.AuthorizationContractReferenceState
                .CreateProposalBySystemContract), proposalCreationInput);
    }

    private void AssertUserDeployContract()
    {
        // Only the symbol of main chain or public side chain is native symbol.
        RequireTokenContractContractAddressSet();
        var primaryTokenSymbol = State.TokenContract.GetPrimaryTokenSymbol.Call(new Empty()).Value;
        if (Context.Variables.NativeSymbol == primaryTokenSymbol)
        {
            return;
        }

        RequireParliamentContractAddressSet();
        var whitelist = State.ParliamentContract.GetProposerWhiteList.Call(new Empty());
        Assert(whitelist.Proposers.Contains(Context.Sender), "No permission.");
    }

    private void RequireTokenContractContractAddressSet()
    {
        if (State.TokenContract.Value == null)
            State.TokenContract.Value =
                Context.GetContractAddressByName(SmartContractConstants.TokenContractSystemName);
    }

    private void AssertContractVersion(string currentVersion, ByteString code, int category)
    {
        var contractVersionCheckResult =
            Context.CheckContractVersion(currentVersion, new SmartContractRegistration
            {
                Code = code,
                Category = category,
                CodeHash = HashHelper.ComputeFrom(code.ToByteArray())
            });
        Assert(contractVersionCheckResult.IsSubsequentVersion,
            $"The version to be deployed is lower than the effective version({currentVersion}), please correct the version number.");
    }

    private void AssertContractNotExists(Hash codeHash)
    {
        Assert(State.SmartContractRegistrations[codeHash] == null, "contract code has already been deployed before.");
    }

    private void AssertInlineDeployOrUpdateUserContract()
    {
        Assert(Context.Origin == Context.Sender || !IsMainChain(),
            "Deploy or update contracts using inline transactions is not allowed.");
    }

    private bool IsMainChain()
    {
        return Context.GetContractAddressByName(SmartContractConstants.TreasuryContractSystemName) != null;
    }

    private void ValidateContractOperation(ContractOperation contractOperation, int currentVersion, Hash codeHash)
    {
        Assert(contractOperation.Deployer != null && !contractOperation.Deployer.Value.IsNullOrEmpty(),
            "Invalid input deploying address.");
        Assert(contractOperation.Salt != null && !contractOperation.Salt.Value.IsNullOrEmpty(), "Invalid input salt.");
        Assert(contractOperation.CodeHash != null && !contractOperation.CodeHash.Value.IsNullOrEmpty(),
            "Invalid input code hash.");
        Assert(!contractOperation.Signature.IsNullOrEmpty(), "Invalid input signature.");

        Assert(contractOperation.Version == currentVersion + 1, "Invalid input version.");
        Assert(contractOperation.ChainId == Context.ChainId, "Invalid input chain id.");
        Assert(contractOperation.CodeHash == codeHash, "Invalid input code hash.");

        var recoveredAddress = RecoverAddressFromSignature(contractOperation);

        Assert(
            recoveredAddress == contractOperation.Deployer ||
            State.SignerMap[contractOperation.Deployer] == recoveredAddress, "Invalid signature.");
    }

    private Address RecoverAddressFromSignature(ContractOperation contractOperation)
    {
        var hash = ComputeContractOperationHash(contractOperation);
        var publicKey = Context.RecoverPublicKey(contractOperation.Signature.ToByteArray(), hash.ToByteArray());

        return Address.FromPublicKey(publicKey);
    }

    private Hash ComputeContractOperationHash(ContractOperation contractOperation)
    {
        return HashHelper.ComputeFrom(new ContractOperation
        {
            ChainId = contractOperation.ChainId,
            CodeHash = contractOperation.CodeHash,
            Deployer = contractOperation.Deployer,
            Salt = contractOperation.Salt,
            Version = contractOperation.Version
        }.ToByteArray());
    }

    private void RemoveOneTimeSigner(Address address)
    {
        State.SignerMap.Remove(address);
    }

    private void AssertContractAddressAvailable(Address deployer, Hash salt)
    {
        var contractAddress = AddressHelper.ComputeContractAddress(deployer, salt);
        Assert(State.ContractInfos[contractAddress] == null, "Contract address exists.");
    }

    private void AssertSameDeployer(Address contractAddress, Address deployer)
    {
        var contractInfo = State.ContractInfos[contractAddress];
        Assert(contractInfo != null, "Contract not exists.");
        Assert(contractInfo.Deployer == deployer, "No permission.");
    }
}

public static class AddressHelper
{
    /// <summary>
    /// </summary>
    /// <returns></returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    private static Address ComputeContractAddress(Hash chainId, long serialNumber)
    {
        var hash = HashHelper.ConcatAndCompute(chainId, HashHelper.ComputeFrom(serialNumber));
        return Address.FromBytes(hash.ToByteArray());
    }

    public static Address ComputeContractAddress(int chainId, long serialNumber)
    {
        return ComputeContractAddress(HashHelper.ComputeFrom(chainId), serialNumber);
    }

    public static Address ComputeContractAddress(Address deployer, Hash salt)
    {
        var hash = HashHelper.ConcatAndCompute(HashHelper.ComputeFrom(deployer), salt);
        return Address.FromBytes(hash.ToByteArray());
    }
}