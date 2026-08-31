using AgenticMES.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgenticMES.Infrastructure.StateMachine;

/// <summary>
/// Extension methods for working with Equipment state machines.
/// </summary>
public static class EquipmentStateMachineExtensions
{
    /// <summary>
    /// Creates a Stateless-based state machine wrapper for an Equipment entity.
    /// </summary>
    public static IEquipmentStateMachine CreateStateMachine(
        this Equipment equipment,
        ILogger<EquipmentStateMachineService> logger)
    {
        return new EquipmentStateMachineService(equipment, logger);
    }

    /// <summary>
    /// Creates a Stateless-based state machine wrapper using a service provider.
    /// </summary>
    public static IEquipmentStateMachine CreateStateMachine(
        this Equipment equipment,
        IServiceProvider serviceProvider)
    {
        var logger = serviceProvider.GetRequiredService<ILogger<EquipmentStateMachineService>>();
        return new EquipmentStateMachineService(equipment, logger);
    }
}
