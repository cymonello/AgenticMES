using AgenticMES.Application.Common.Interfaces;
using AgenticMES.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace AgenticMES.Infrastructure.StateMachine;

/// <summary>
/// Binds an <see cref="Equipment"/> instance to the Stateless graph.
/// </summary>
public sealed class EquipmentStateMachineFactory(ILogger<EquipmentStateMachineService> logger)
    : IEquipmentStateMachineFactory
{
    public IEquipmentStateMachine Create(Equipment equipment) =>
        new EquipmentStateMachineService(equipment, logger);
}
