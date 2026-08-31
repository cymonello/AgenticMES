# State Machine Refactoring: Stateless Framework

## Overview

This branch (`state_machine_refactor`) refactors the Equipment state machine from a manual pattern-matching implementation to use the **Stateless** framework.

## What Changed

### New Files

1. **`AgenticMES.Domain/Enums/EquipmentTrigger.cs`**
   - New enum defining business triggers (Start, Stop, Fault, EnterSetup, etc.)
   - Provides more semantic API than direct state transitions

2. **`AgenticMES.Infrastructure/StateMachine/EquipmentStateMachineFactory.cs`**
   - Factory that configures the Stateless state machine
   - Defines all valid ISA-95 equipment transitions
   - Provides `GenerateDotGraph()` for visualization

3. **`AgenticMES.Infrastructure/StateMachine/IEquipmentStateMachine.cs`**
   - Interface for state machine operations
   - Keeps Infrastructure concerns separate from Domain

4. **`AgenticMES.Infrastructure/StateMachine/EquipmentStateMachineService.cs`**
   - Concrete implementation wrapping Stateless
   - Handles logging, parameterized triggers, and transition hooks

5. **`AgenticMES.Infrastructure/StateMachine/EquipmentStateMachineExtensions.cs`**
   - Extension methods for easy state machine creation
   - Simplifies integration in application code

6. **`AgenticMES.Infrastructure/StateMachine/README.md`**
   - Complete documentation with usage examples
   - Migration guide and testing patterns

### Modified Files

- **`AgenticMES.Domain/Entities/Equipment.cs`**
  - Added XML documentation to `CanTransitionTo()` method
  - No breaking changes - existing API preserved for backward compatibility

## Why Stateless?

The Stateless package (v5.20.1) was already referenced in `AgenticMES.Infrastructure.csproj` as recommended in `.cursorrules`:

> "Use `Stateless` state machines for industrial machine state management"

### Benefits

1. **Clearer Intent**: `Fire(Trigger.Start)` vs `TryTransitionTo(State.Running)`
2. **Automatic Validation**: Framework enforces legal transitions
3. **Entry/Exit Hooks**: Clean side-effect management
4. **Visualization**: Auto-generate state diagrams
5. **Maintainability**: Centralized transition logic
6. **Testability**: Configuration is separate from entity
7. **Extensibility**: Easy to add guards, hierarchical states, async actions

## Usage Examples

### Before (Manual State Machine)

```csharp
var equipment = await repository.GetByCodeAsync("CNC-01");
var result = equipment.TryTransitionTo(EquipmentState.Running);
if (result.IsSuccess)
{
    equipment.AssignWorkOrder(workOrderId);
    await repository.UpdateAsync(equipment);
}
```

### After (Stateless Framework)

```csharp
var equipment = await repository.GetByCodeAsync("CNC-01");
var stateMachine = equipment.CreateStateMachine(logger);

if (stateMachine.CanFire(EquipmentTrigger.Start))
{
    var result = stateMachine.Fire(EquipmentTrigger.Start);
    if (result.IsSuccess)
    {
        equipment.AssignWorkOrder(workOrderId);
        await repository.UpdateAsync(equipment);
    }
}
```

### Parameterized Triggers (Fault with Reason)

```csharp
var stateMachine = equipment.CreateStateMachine(logger);
var result = stateMachine.Fire(EquipmentTrigger.Fault, "Thermal runaway - coolant pressure dropped below 10 PSI");
```

### Visualize State Machine

```csharp
string dotGraph = EquipmentStateMachineFactory.GenerateDotGraph();
// Paste output into: https://dreampuf.github.io/GraphvizOnline/
```

## Integration Points

The refactoring provides **two usage patterns**:

1. **Legacy Path** (backward compatible):
   - Continue using `equipment.Start()`, `equipment.Stop()`, `equipment.Fault(reason)`
   - These methods internally call `TryTransitionTo()`

2. **New Path** (recommended):
   - Create state machine: `var sm = equipment.CreateStateMachine(logger)`
   - Fire triggers: `sm.Fire(EquipmentTrigger.Start)`
   - More testable, loggable, and extensible

## Testing Strategy

### Unit Tests for State Machine Configuration

```csharp
[Theory]
[InlineData(EquipmentState.Idle, EquipmentTrigger.Start, EquipmentState.Running)]
[InlineData(EquipmentState.Running, EquipmentTrigger.Stop, EquipmentState.Idle)]
[InlineData(EquipmentState.Setup, EquipmentTrigger.CompleteSetup, EquipmentState.Running)]
public void Should_Transition_Correctly(EquipmentState initial, EquipmentTrigger trigger, EquipmentState expected)
{
    var equipment = CreateEquipment(initial);
    var sm = equipment.CreateStateMachine(logger);
    
    sm.Fire(trigger);
    
    Assert.Equal(expected, equipment.State);
}
```

### Integration Tests with Equipment Repository

```csharp
[Fact]
public async Task Should_Update_Equipment_State_In_Repository()
{
    var equipment = await repository.GetByCodeAsync("CNC-01");
    var sm = equipment.CreateStateMachine(logger);
    
    sm.Fire(EquipmentTrigger.Start);
    await repository.UpdateAsync(equipment);
    
    var reloaded = await repository.GetByCodeAsync("CNC-01");
    Assert.Equal(EquipmentState.Running, reloaded.State);
}
```

## State Transition Diagram

Current ISA-95 equipment state machine:

```
       ┌─────────┐
       │  Idle   │◄────────────────┐
       └────┬────┘                 │
            │                      │
    ┌───────┼──────────┬───────────┤
    │       │          │           │
    ▼       ▼          ▼           │
┌───────┐ ┌────┐  ┌──────────┐    │
│ Setup │ │Run │  │Maintenance│    │
└───┬───┘ └─┬──┘  └─────┬────┘    │
    │       │           │          │
    └───┬───┴─────┬─────┴──────────┘
        │         │
        ▼         ▼
    ┌─────────────┐
    │   Faulted   │
    └─────────────┘
```

**Valid Transitions:**
- **Idle** → Running, Setup, Maintenance, Faulted
- **Running** → Idle, Faulted, Maintenance, Setup
- **Faulted** → Idle, Maintenance
- **Maintenance** → Idle
- **Setup** → Idle, Running, Faulted

## Migration Checklist

- [x] Install Stateless package (already present)
- [x] Create `EquipmentTrigger` enum in Domain
- [x] Create `EquipmentStateMachineFactory` in Infrastructure
- [x] Create `IEquipmentStateMachine` interface
- [x] Create `EquipmentStateMachineService` implementation
- [x] Add extension methods for easy usage
- [x] Document usage patterns and examples
- [x] Create `IEquipmentStateMachineFactory` in Application layer
- [x] Implement factory in Infrastructure layer
- [x] Update `ChangeMachineStateCommandHandler` to use Stateless
- [x] Update `RerouteWorkOrderCommandHandler` to use Stateless
- [x] Register factory in DI container (Program.cs)
- [x] Verify all projects build successfully
- [ ] Add unit tests for state machine configuration
- [ ] Add integration tests with repository
- [ ] Test end-to-end with console app demo

## Next Steps

1. **Review the refactoring** and provide feedback
2. **Test the state machine** with existing integration tests
3. **Migrate existing code** to use trigger-based API where appropriate
4. **Generate state diagram** and add to documentation
5. **Consider adding guards** for conditional transitions (e.g., "can't start without work order")

## Notes

- The `Equipment` entity maintains backward compatibility - no breaking changes
- The Stateless implementation is in Infrastructure, keeping Domain clean
- Both APIs (legacy `TryTransitionTo` and new `Fire(trigger)`) coexist
- The new API is recommended for new development

## Questions?

See `AgenticMES.Infrastructure/StateMachine/README.md` for detailed usage examples and patterns.
