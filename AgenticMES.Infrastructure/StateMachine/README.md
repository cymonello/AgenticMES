# Equipment State Machine (Stateless Framework)

This folder contains the **Stateless framework** implementation of the ISA-95 Equipment state machine.

## Architecture

The refactored state machine follows **Clean Architecture** principles:

- **Domain Layer** (`AgenticMES.Domain`): Contains `Equipment` entity, `EquipmentState` enum, and `EquipmentTrigger` enum
- **Infrastructure Layer** (`AgenticMES.Infrastructure.StateMachine`): Contains Stateless framework implementation

This keeps the Domain layer free of external dependencies while leveraging Stateless for robust state management.

## Components

### 1. `EquipmentTrigger` (Domain Enum)
Business-meaningful triggers that cause state transitions:
- `Start` - Begin production
- `Stop` - Stop production gracefully
- `Fault` - Enter fault state
- `EnterSetup` - Begin changeover/setup
- `EnterMaintenance` - Enter maintenance mode
- `Reset` - Return to Idle from Fault/Maintenance/Setup
- `CompleteSetup` - Finish setup and start production

### 2. `EquipmentStateMachineFactory`
Factory class that configures the Stateless state machine with all valid ISA-95 transitions:

```
Idle ──────→ Running, Setup, Maintenance, Faulted
Running ────→ Idle, Faulted, Maintenance, Setup
Faulted ────→ Idle, Maintenance
Maintenance → Idle
Setup ──────→ Idle, Running, Faulted
```

### 3. `IEquipmentStateMachine` Interface
Clean interface for interacting with the state machine:
- `CanFire(trigger)` - Check if a trigger is valid
- `Fire(trigger)` - Execute a state transition
- `Fire<TParam>(trigger, parameter)` - Execute parameterized transition (e.g., Fault with reason)
- `ToDotGraph()` - Generate visualization

### 4. `EquipmentStateMachineService`
Concrete implementation wrapping Stateless framework with:
- Automatic logging of transitions
- Parameter support for Fault trigger (includes reason)
- Entry/exit action hooks
- Integration with Equipment entity

## Usage Examples

### Basic Usage

```csharp
// Create state machine for an equipment entity
var equipment = new Equipment(/* ... */);
var stateMachine = equipment.CreateStateMachine(logger);

// Check if transition is valid
if (stateMachine.CanFire(EquipmentTrigger.Start))
{
    var result = stateMachine.Fire(EquipmentTrigger.Start);
    if (result.IsSuccess)
    {
        Console.WriteLine("Equipment started successfully");
    }
}

// Fire parameterized trigger (Fault with reason)
var faultResult = stateMachine.Fire(EquipmentTrigger.Fault, "Thermal runaway detected");
```

### With Dependency Injection

```csharp
// In a service class
public class MachineControlService
{
    private readonly ILogger<EquipmentStateMachineService> _logger;
    
    public MachineControlService(ILogger<EquipmentStateMachineService> logger)
    {
        _logger = logger;
    }
    
    public DomainResult StartEquipment(Equipment equipment, Guid workOrderId)
    {
        var stateMachine = equipment.CreateStateMachine(_logger);
        
        if (!stateMachine.CanFire(EquipmentTrigger.Start))
        {
            return DomainResult.Failure($"Cannot start {equipment.EquipmentCode}");
        }
        
        var result = stateMachine.Fire(EquipmentTrigger.Start);
        if (result.IsSuccess)
        {
            equipment.AssignWorkOrder(workOrderId);
        }
        
        return result;
    }
}
```

### Generate State Diagram

```csharp
// Generate DOT graph for visualization
string dotGraph = EquipmentStateMachineFactory.GenerateDotGraph();
Console.WriteLine(dotGraph);

// Visualize at: https://dreampuf.github.io/GraphvizOnline/
```

## Benefits Over Manual Implementation

1. **Explicit Triggers**: `Fire(Trigger.Start)` is more business-meaningful than `TryTransitionTo(State.Running)`
2. **Automatic Validation**: Stateless enforces valid transitions automatically
3. **Entry/Exit Actions**: Clean hooks for side effects (clearing work orders, logging, etc.)
4. **Parameterized Triggers**: Pass data through transitions (e.g., fault reason)
5. **Visualization**: Generate state diagrams automatically for documentation
6. **Testing**: State machine configuration is separate and easily testable
7. **Maintainability**: Centralized transition logic, easier to modify rules

## Migration Path

The `Equipment` entity still maintains its original `TryTransitionTo()` method for backward compatibility. New code should use the state machine service for richer semantics and better maintainability.

### Before (Manual):
```csharp
var result = equipment.TryTransitionTo(EquipmentState.Running);
```

### After (Stateless):
```csharp
var stateMachine = equipment.CreateStateMachine(logger);
var result = stateMachine.Fire(EquipmentTrigger.Start);
```

## Testing

Example unit test structure:

```csharp
[Fact]
public void Should_Transition_From_Idle_To_Running_On_Start()
{
    // Arrange
    var equipment = CreateTestEquipment(EquipmentState.Idle);
    var stateMachine = equipment.CreateStateMachine(logger);
    
    // Act
    var result = stateMachine.Fire(EquipmentTrigger.Start);
    
    // Assert
    Assert.True(result.IsSuccess);
    Assert.Equal(EquipmentState.Running, equipment.State);
}

[Fact]
public void Should_Reject_Start_From_Faulted_State()
{
    // Arrange
    var equipment = CreateTestEquipment(EquipmentState.Faulted);
    var stateMachine = equipment.CreateStateMachine(logger);
    
    // Act
    var canStart = stateMachine.CanFire(EquipmentTrigger.Start);
    
    // Assert
    Assert.False(canStart);
}
```

## Future Enhancements

- **Hierarchical States**: Add sub-states (e.g., Running.Normal, Running.Degraded)
- **Guards**: Conditional transitions based on equipment properties
- **Async Actions**: Support async entry/exit actions for I/O operations
- **State Persistence**: Integrate with event sourcing for full audit trail
- **Reentrant Transitions**: Support same-state transitions for certain triggers
