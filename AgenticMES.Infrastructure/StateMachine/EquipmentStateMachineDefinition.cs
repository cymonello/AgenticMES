using AgenticMES.Domain.Enums;
using Stateless;
using Stateless.Graph;

namespace AgenticMES.Infrastructure.StateMachine;

/// <summary>
/// ISA-95 equipment graph. Single source of truth for legal transitions.
/// </summary>
internal static class EquipmentStateMachineDefinition
{
    public static StateMachine<EquipmentState, EquipmentTrigger> Create(
        Func<EquipmentState> getState,
        Action<EquipmentState> setState)
    {
        var machine = new StateMachine<EquipmentState, EquipmentTrigger>(getState, setState);

        machine.Configure(EquipmentState.Idle)
            .Permit(EquipmentTrigger.Start, EquipmentState.Running)
            .Permit(EquipmentTrigger.EnterSetup, EquipmentState.Setup)
            .Permit(EquipmentTrigger.EnterMaintenance, EquipmentState.Maintenance)
            .Permit(EquipmentTrigger.Fault, EquipmentState.Faulted);

        machine.Configure(EquipmentState.Running)
            .Permit(EquipmentTrigger.Stop, EquipmentState.Idle)
            .Permit(EquipmentTrigger.Fault, EquipmentState.Faulted)
            .Permit(EquipmentTrigger.EnterMaintenance, EquipmentState.Maintenance)
            .Permit(EquipmentTrigger.EnterSetup, EquipmentState.Setup);

        machine.Configure(EquipmentState.Faulted)
            .Permit(EquipmentTrigger.Reset, EquipmentState.Idle)
            .Permit(EquipmentTrigger.EnterMaintenance, EquipmentState.Maintenance);

        machine.Configure(EquipmentState.Maintenance)
            .Permit(EquipmentTrigger.Reset, EquipmentState.Idle);

        machine.Configure(EquipmentState.Setup)
            .Permit(EquipmentTrigger.Reset, EquipmentState.Idle)
            .Permit(EquipmentTrigger.CompleteSetup, EquipmentState.Running)
            .Permit(EquipmentTrigger.Fault, EquipmentState.Faulted);

        return machine;
    }

    public static string ToDotGraph()
    {
        var machine = Create(() => EquipmentState.Idle, _ => { });
        return UmlDotGraph.Format(machine.GetInfo());
    }
}
