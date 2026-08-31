using AgenticMES.Application.Common.Interfaces;
using AgenticMES.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace AgenticMES.Infrastructure.StateMachine;

/// <summary>
/// Factory implementation for creating Equipment state machines using Stateless framework.
/// </summary>
public sealed class EquipmentStateMachineFactoryImpl : IEquipmentStateMachineFactory
{
    private readonly ILogger<EquipmentStateMachineService> _logger;

    public EquipmentStateMachineFactoryImpl(ILogger<EquipmentStateMachineService> logger)
    {
        _logger = logger;
    }

    public IEquipmentStateMachineWrapper Create(Equipment equipment)
    {
        return new EquipmentStateMachineService(equipment, _logger);
    }
}
