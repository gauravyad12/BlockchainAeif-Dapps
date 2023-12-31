﻿using AElf.Blockchains.BasicBaseChain;
using AElf.Kernel.SmartContract.Application;
using AElf.Modularity;
using AElf.OS.Node.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Volo.Abp.Modularity;

namespace AElf.Blockchains.SideChain;

[DependsOn(
    typeof(BasicBaseChainAElfModule)
)]
public class SideChainAElfModule : AElfModule
{
    public SideChainAElfModule()
    {
        Logger = NullLogger<SideChainAElfModule>.Instance;
    }

    public ILogger<SideChainAElfModule> Logger { get; set; }

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddTransient<IContractDeploymentListProvider, SideChainContractDeploymentListProvider>();
        context.Services.AddTransient<IGenesisSmartContractDtoProvider, SideChainGenesisSmartContractDtoProvider>();
    }
}