using AgenticMES.Domain.Entities;
using AgenticMES.Domain.Enums;
using AgenticMES.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace AgenticMES.Infrastructure.StateMachine.Examples;

/// <summary>
/// Demonstration of Equipment state machine usage with the Stateless framework.
/// This class is for reference only and is not executed in the application.
/// </summary>
public static class StateMachineUsageExample
{
    public static void DemonstrateBasicUsage(ILogger<EquipmentStateMachineService> logger)
    {
        // Create an equipment entity
        var equipment = new Equipment(
            id: Guid.NewGuid(),
            equipmentCode: "CNC-01",
            name: "CNC Milling Machine 01",
            level: EquipmentLevel.Equipment,
            hierarchy: new EquipmentHierarchy(
                EnterpriseId: "ACME",
                SiteId: "Factory-01",
                AreaId: "Machining",
                WorkCenterId: "CNC-Cell-A"),
            state: EquipmentState.Idle);

        // Create state machine wrapper
        var stateMachine = equipment.CreateStateMachine(logger);

        // Example 1: Check if a trigger can be fired
        if (stateMachine.CanFire(EquipmentTrigger.Start))
        {
            Console.WriteLine("✓ Can start the equipment");
        }

        // Example 2: Fire a trigger to transition state
        var startResult = stateMachine.Fire(EquipmentTrigger.Start);
        if (startResult.IsSuccess)
        {
            Console.WriteLine($"Equipment state: {equipment.State}"); // Output: Running
        }

        // Example 3: Attempt invalid transition
        if (!stateMachine.CanFire(EquipmentTrigger.Start))
        {
            Console.WriteLine("✗ Cannot start - already running");
        }

        // Example 4: Fault with reason (parameterized trigger)
        var faultResult = stateMachine.Fire(EquipmentTrigger.Fault, "Thermal runaway detected - coolant pressure dropped");
        if (faultResult.IsSuccess)
        {
            Console.WriteLine($"Equipment faulted: {equipment.State}"); // Output: Faulted
        }

        // Example 5: Reset from fault
        var resetResult = stateMachine.Fire(EquipmentTrigger.Reset);
        if (resetResult.IsSuccess)
        {
            Console.WriteLine($"Equipment reset: {equipment.State}"); // Output: Idle
        }
    }

    public static void DemonstrateWorkOrderWorkflow(ILogger<EquipmentStateMachineService> logger)
    {
        var equipment = CreateSampleEquipment();
        var stateMachine = equipment.CreateStateMachine(logger);
        var workOrderId = Guid.NewGuid();

        Console.WriteLine("=== Production Workflow ===\n");

        // Step 1: Equipment is Idle, assign work order
        Console.WriteLine($"Initial state: {equipment.State}");
        equipment.AssignWorkOrder(workOrderId);
        Console.WriteLine($"Work order {workOrderId:N} assigned");

        // Step 2: Start production
        if (stateMachine.CanFire(EquipmentTrigger.Start))
        {
            stateMachine.Fire(EquipmentTrigger.Start);
            Console.WriteLine($"Production started: {equipment.State}");
        }

        // Step 3: Equipment experiences a fault
        stateMachine.Fire(EquipmentTrigger.Fault, "Spindle overheating detected");
        Console.WriteLine($"Fault occurred: {equipment.State}");

        // Step 4: Enter maintenance
        stateMachine.Fire(EquipmentTrigger.EnterMaintenance);
        Console.WriteLine($"Entered maintenance: {equipment.State}");

        // Step 5: Complete maintenance and return to Idle
        stateMachine.Fire(EquipmentTrigger.Reset);
        Console.WriteLine($"Maintenance complete: {equipment.State}");
    }

    public static void DemonstrateSetupWorkflow(ILogger<EquipmentStateMachineService> logger)
    {
        var equipment = CreateSampleEquipment();
        var stateMachine = equipment.CreateStateMachine(logger);

        Console.WriteLine("=== Setup/Changeover Workflow ===\n");

        // Step 1: Enter setup mode for tooling change
        stateMachine.Fire(EquipmentTrigger.EnterSetup);
        Console.WriteLine($"Entered setup: {equipment.State}");

        // Step 2: Complete setup and start running
        stateMachine.Fire(EquipmentTrigger.CompleteSetup);
        Console.WriteLine($"Setup complete, production started: {equipment.State}");
    }

    public static string GenerateStateDiagram()
    {
        // Generate DOT graph for visualization
        // Paste output into: https://dreampuf.github.io/GraphvizOnline/
        return EquipmentStateMachineFactory.GenerateDotGraph();
    }

    private static Equipment CreateSampleEquipment()
    {
        return new Equipment(
            id: Guid.NewGuid(),
            equipmentCode: "CNC-01",
            name: "CNC Milling Machine 01",
            level: EquipmentLevel.Equipment,
            hierarchy: new EquipmentHierarchy(
                EnterpriseId: "ACME",
                SiteId: "Factory-01",
                AreaId: "Machining",
                WorkCenterId: "CNC-Cell-A"),
            state: EquipmentState.Idle);
    }
}
