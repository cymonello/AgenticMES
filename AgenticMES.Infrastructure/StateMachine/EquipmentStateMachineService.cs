using AgenticMES.Application.Common.Interfaces;
using AgenticMES.Domain.Common;
using AgenticMES.Domain.Entities;
using AgenticMES.Domain.Enums;
using Microsoft.Extensions.Logging;
using Stateless;

namespace AgenticMES.Infrastructure.StateMachine;

/// <summary>
/// Executes ISA-95 triggers against one equipment aggregate.
/// </summary>
public sealed class EquipmentStateMachineService : IEquipmentStateMachine
{
    private readonly StateMachine<EquipmentState, EquipmentTrigger> _stateMachine;
    private readonly ILogger<EquipmentStateMachineService> _logger;
    private string? _pendingFaultReason;

    public Equipment Equipment { get; }

    public EquipmentStateMachineService(
        Equipment equipment,
        ILogger<EquipmentStateMachineService> logger)
    {
        Equipment = equipment;
        _logger = logger;
        _stateMachine = EquipmentStateMachineDefinition.Create(
            () => Equipment.State,
            newState => Equipment.ApplyState(newState, _pendingFaultReason));
    }

    public bool CanFire(EquipmentTrigger trigger) => _stateMachine.CanFire(trigger);

    public DomainResult Fire(EquipmentTrigger trigger) => FireCore(trigger, faultReason: null);

    public DomainResult Fire(EquipmentTrigger trigger, string parameter)
    {
        if (trigger is not EquipmentTrigger.Fault)
        {
            return DomainResult.Failure($"Trigger {trigger} does not accept a parameter.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(parameter);
        return FireCore(trigger, parameter.Trim());
    }

    private DomainResult FireCore(EquipmentTrigger trigger, string? faultReason)
    {
        if (!CanFire(trigger))
        {
            var error =
                $"Cannot fire trigger {trigger} on equipment {Equipment.EquipmentCode} in state {Equipment.State}.";
            _logger.LogWarning("{Error}", error);
            return DomainResult.Failure(error);
        }

        var previous = Equipment.State;
        _pendingFaultReason = faultReason;

        try
        {
            _stateMachine.Fire(trigger);
            _logger.LogInformation(
                "Equipment {EquipmentCode} {Trigger}: {PreviousState} → {NewState}",
                Equipment.EquipmentCode,
                trigger,
                previous,
                Equipment.State);
            return DomainResult.Success();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "State machine transition failed for {EquipmentCode}", Equipment.EquipmentCode);
            return DomainResult.Failure($"Transition failed: {ex.Message}");
        }
        finally
        {
            _pendingFaultReason = null;
        }
    }
}
