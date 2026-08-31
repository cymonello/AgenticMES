using AgenticMES.Domain.Enums;
using Stateless;
using Stateless.Graph;

namespace AgenticMES.Infrastructure.StateMachine;

/// <summary>
/// Factory for creating ISA-95 compliant Equipment state machines using the Stateless framework.
/// Enforces valid transitions and provides hooks for entry/exit actions.
/// </summary>
public static class EquipmentStateMachineFactory
{
    /// <summary>
    /// Creates a configured state machine for equipment lifecycle management.
    /// </summary>
    /// <param name="getState">Function to retrieve current equipment state</param>
    /// <param name="setState">Action to update equipment state</param>
    /// <param name="onTransition">Optional callback invoked after successful state transitions</param>
    /// <returns>Configured StateMachine instance</returns>
    public static StateMachine<EquipmentState, EquipmentTrigger> Create(
        Func<EquipmentState> getState,
        Action<EquipmentState> setState,
        Action<EquipmentState, EquipmentState>? onTransition = null)
    {
        var machine = new StateMachine<EquipmentState, EquipmentTrigger>(getState, setState);

        // Configure Idle state - equipment is ready but not producing
        machine.Configure(EquipmentState.Idle)
            .Permit(EquipmentTrigger.Start, EquipmentState.Running)
            .Permit(EquipmentTrigger.EnterSetup, EquipmentState.Setup)
            .Permit(EquipmentTrigger.EnterMaintenance, EquipmentState.Maintenance)
            .Permit(EquipmentTrigger.Fault, EquipmentState.Faulted);

        // Configure Running state - active production
        machine.Configure(EquipmentState.Running)
            .Permit(EquipmentTrigger.Stop, EquipmentState.Idle)
            .Permit(EquipmentTrigger.Fault, EquipmentState.Faulted)
            .Permit(EquipmentTrigger.EnterMaintenance, EquipmentState.Maintenance)
            .Permit(EquipmentTrigger.EnterSetup, EquipmentState.Setup);

        // Configure Faulted state - equipment failure requiring attention
        machine.Configure(EquipmentState.Faulted)
            .Permit(EquipmentTrigger.Reset, EquipmentState.Idle)
            .Permit(EquipmentTrigger.EnterMaintenance, EquipmentState.Maintenance);

        // Configure Maintenance state - planned or reactive maintenance
        machine.Configure(EquipmentState.Maintenance)
            .Permit(EquipmentTrigger.Reset, EquipmentState.Idle);

        // Configure Setup state - changeover, tooling, preparation
        machine.Configure(EquipmentState.Setup)
            .Permit(EquipmentTrigger.Reset, EquipmentState.Idle)
            .Permit(EquipmentTrigger.CompleteSetup, EquipmentState.Running)
            .Permit(EquipmentTrigger.Fault, EquipmentState.Faulted);

        // Register global transition callback if provided
        if (onTransition is not null)
        {
            machine.OnTransitioned(t => onTransition(t.Source, t.Destination));
        }

        return machine;
    }

    /// <summary>
    /// Generates a DOT graph representation of the state machine for visualization.
    /// Use tools like Graphviz to render: https://dreampuf.github.io/GraphvizOnline/
    /// </summary>
    public static string GenerateDotGraph()
    {
        // Create a temporary state machine for graph generation
        var tempMachine = Create(() => EquipmentState.Idle, _ => { });
        return UmlDotGraph.Format(tempMachine.GetInfo());
    }
}
