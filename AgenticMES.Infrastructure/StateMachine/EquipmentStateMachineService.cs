using AgenticMES.Application.Common.Interfaces;
using AgenticMES.Domain.Common;
using AgenticMES.Domain.Entities;
using AgenticMES.Domain.Enums;
using Microsoft.Extensions.Logging;
using Stateless;
using Stateless.Graph;

namespace AgenticMES.Infrastructure.StateMachine;

/// <summary>
/// Stateless-based state machine service for Equipment entity management.
/// Wraps the Stateless framework to keep Domain layer free of external dependencies.
/// </summary>
public sealed class EquipmentStateMachineService : IEquipmentStateMachine, IEquipmentStateMachineWrapper
{
    private readonly StateMachine<EquipmentState, EquipmentTrigger> _stateMachine;
    private readonly StateMachine<EquipmentState, EquipmentTrigger>.TriggerWithParameters<string> _faultTrigger;
    private readonly ILogger<EquipmentStateMachineService> _logger;

    public Equipment Equipment { get; }

    public EquipmentStateMachineService(
        Equipment equipment,
        ILogger<EquipmentStateMachineService> logger)
    {
        Equipment = equipment;
        _logger = logger;

        _stateMachine = EquipmentStateMachineFactory.Create(
            getState: () => Equipment.State,
            setState: newState =>
            {
                Equipment.TryTransitionTo(newState);
                _logger.LogInformation(
                    "Equipment {EquipmentCode} transitioned to {State}",
                    Equipment.EquipmentCode,
                    newState);
            },
            onTransition: OnTransitionCompleted);

        // Configure parameterized trigger for Fault (accepts reason string)
        _faultTrigger = _stateMachine.SetTriggerParameters<string>(EquipmentTrigger.Fault);
    }

    public bool CanFire(EquipmentTrigger trigger)
    {
        return _stateMachine.CanFire(trigger);
    }

    public DomainResult Fire(EquipmentTrigger trigger)
    {
        if (!CanFire(trigger))
        {
            var error = $"Cannot fire trigger {trigger} on equipment {Equipment.EquipmentCode} in state {Equipment.State}";
            _logger.LogWarning(error);
            return DomainResult.Failure(error);
        }

        try
        {
            _stateMachine.Fire(trigger);
            return DomainResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "State machine transition failed for {EquipmentCode}", Equipment.EquipmentCode);
            return DomainResult.Failure($"Transition failed: {ex.Message}");
        }
    }

    public DomainResult Fire<TParam>(EquipmentTrigger trigger, TParam parameter)
    {
        if (trigger == EquipmentTrigger.Fault && parameter is string reason)
        {
            return Fire(trigger, reason);
        }

        return DomainResult.Failure($"Parameterized trigger {trigger} with type {typeof(TParam).Name} is not supported");
    }

    public DomainResult Fire(EquipmentTrigger trigger, string parameter)
    {
        if (trigger == EquipmentTrigger.Fault)
        {
            if (!CanFire(trigger))
            {
                var error = $"Cannot fire trigger {trigger} on equipment {Equipment.EquipmentCode} in state {Equipment.State}";
                _logger.LogWarning(error);
                return DomainResult.Failure(error);
            }

            try
            {
                _stateMachine.Fire(_faultTrigger, parameter);
                return DomainResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fault transition failed for {EquipmentCode}", Equipment.EquipmentCode);
                return DomainResult.Failure($"Fault transition failed: {ex.Message}");
            }
        }

        return DomainResult.Failure($"Parameterized trigger {trigger} is not supported");
    }

    public string ToDotGraph()
    {
        return UmlDotGraph.Format(_stateMachine.GetInfo());
    }

    private void OnTransitionCompleted(EquipmentState source, EquipmentState destination)
    {
        _logger.LogInformation(
            "State transition completed: {EquipmentCode} {Source} → {Destination}",
            Equipment.EquipmentCode,
            source,
            destination);

        // Handle automatic cleanup on state transitions
        if (destination == EquipmentState.Idle)
        {
            // Clear work order and fault reason when returning to Idle
            Equipment.ClearWorkOrderAssignment();
        }

        if (source == EquipmentState.Faulted && destination != EquipmentState.Faulted)
        {
            // Fault reason cleared via Equipment.TryTransitionTo logic
        }
    }
}
