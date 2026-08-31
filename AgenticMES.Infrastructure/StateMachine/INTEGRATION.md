# State Machine Integration Guide

## Overview

The Equipment state machine has been **fully integrated** with the Application layer. All state transitions now go through the Stateless framework instead of manual pattern matching.

## Integration Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        Application Layer                         │
│  ┌────────────────────────────────────────────────────────┐    │
│  │  ChangeMachineStateCommandHandler                       │    │
│  │  RerouteWorkOrderCommandHandler                         │    │
│  └──────────────────┬─────────────────────────────────────┘    │
│                     │                                            │
│                     │ uses                                       │
│                     ▼                                            │
│  ┌────────────────────────────────────────────────────────┐    │
│  │  IEquipmentStateMachineFactory (interface)             │    │
│  │  IEquipmentStateMachineWrapper (interface)             │    │
│  └────────────────────────────────────────────────────────┘    │
└──────────────────────┬──────────────────────────────────────────┘
                       │
                       │ implemented by
                       ▼
┌─────────────────────────────────────────────────────────────────┐
│                      Infrastructure Layer                        │
│  ┌────────────────────────────────────────────────────────┐    │
│  │  EquipmentStateMachineFactoryImpl                       │    │
│  │  EquipmentStateMachineService                           │    │
│  │  └── wraps Stateless.StateMachine<TState, TTrigger>    │    │
│  └────────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────────┘
```

## What Changed

### 1. Application Layer (AgenticMES.Application)

#### New Interface: `IEquipmentStateMachineFactory`

```csharp
public interface IEquipmentStateMachineFactory
{
    IEquipmentStateMachineWrapper Create(Equipment equipment);
}

public interface IEquipmentStateMachineWrapper
{
    Equipment Equipment { get; }
    bool CanFire(EquipmentTrigger trigger);
    DomainResult Fire(EquipmentTrigger trigger);
    DomainResult Fire(EquipmentTrigger trigger, string parameter);
}
```

This keeps the Application layer independent of Stateless framework while allowing trigger-based state management.

#### Updated Command Handlers

**ChangeMachineStateCommandHandler:**
- Now injects `IEquipmentStateMachineFactory`
- Uses `MapStateToTrigger()` to convert target states to triggers
- Calls `stateMachine.Fire(trigger)` instead of `equipment.Start()`, `equipment.Stop()`, etc.
- Validates transitions with `stateMachine.CanFire(trigger)`

**Before:**
```csharp
var transition = equipment.Stop();
```

**After:**
```csharp
var stateMachine = stateMachineFactory.Create(equipment);
var transition = stateMachine.Fire(EquipmentTrigger.Stop);
```

**RerouteWorkOrderCommandHandler:**
- Also injects `IEquipmentStateMachineFactory`
- Uses state machine for stopping equipment during reroute operations

### 2. Infrastructure Layer (AgenticMES.Infrastructure)

#### New Factory Implementation: `EquipmentStateMachineFactoryImpl`

```csharp
public sealed class EquipmentStateMachineFactoryImpl : IEquipmentStateMachineFactory
{
    private readonly ILogger<EquipmentStateMachineService> _logger;

    public IEquipmentStateMachineWrapper Create(Equipment equipment)
    {
        return new EquipmentStateMachineService(equipment, _logger);
    }
}
```

#### Updated State Machine Service

`EquipmentStateMachineService` now implements both:
- `IEquipmentStateMachine` (Infrastructure-specific)
- `IEquipmentStateMachineWrapper` (Application interface)

Added overload for parameterized triggers:
```csharp
public DomainResult Fire(EquipmentTrigger trigger, string parameter)
{
    if (trigger == EquipmentTrigger.Fault)
    {
        _stateMachine.Fire(_faultTrigger, parameter);
        return DomainResult.Success();
    }
    // ...
}
```

### 3. Dependency Injection (Program.cs)

Registered the state machine factory:

```csharp
using AgenticMES.Infrastructure.StateMachine;

// State machine factory (Stateless framework)
builder.Services.AddSingleton<IEquipmentStateMachineFactory, EquipmentStateMachineFactoryImpl>();
```

## State to Trigger Mapping

The `MapStateToTrigger()` method converts state transitions to triggers:

| Current State | Target State | Trigger          |
|--------------|-------------|------------------|
| Running      | Idle        | Stop             |
| Any          | Idle        | Reset            |
| Any          | Running     | Start            |
| Any          | Faulted     | Fault            |
| Any          | Maintenance | EnterMaintenance |
| Any          | Setup       | EnterSetup       |

## Benefits of This Integration

1. **Clean Architecture Preserved**
   - Application layer depends on interfaces only
   - Infrastructure provides Stateless implementation
   - Domain remains completely independent

2. **Trigger-Based Semantics**
   - `Fire(Trigger.Stop)` is more business-meaningful than `TryTransitionTo(State.Idle)`
   - Triggers represent actions, not destination states

3. **Automatic Validation**
   - Stateless enforces valid transitions
   - No need for manual `CanTransitionTo()` checks (though Equipment still has it for backward compatibility)

4. **Centralized Configuration**
   - All state machine rules in `EquipmentStateMachineFactory`
   - Easy to modify transition rules
   - Single source of truth

5. **Logging and Observability**
   - All transitions logged automatically via `ILogger`
   - Entry/exit actions for side effects
   - Full audit trail

6. **Testability**
   - State machine configuration is separate and testable
   - Can mock `IEquipmentStateMachineFactory` in tests
   - Clear separation of concerns

## Usage in Command Handlers

### Example: Change Machine State

```csharp
public class ChangeMachineStateCommandHandler
{
    private readonly IEquipmentStateMachineFactory _stateMachineFactory;

    public async Task<ChangeMachineStateResult> HandleAsync(
        ChangeMachineStateCommand command,
        CancellationToken cancellationToken)
    {
        // Get equipment from repository
        var equipment = await _equipmentRepository.GetByIdAsync(
            command.EquipmentId, 
            cancellationToken);

        // Create state machine
        var stateMachine = _stateMachineFactory.Create(equipment);
        
        // Map target state to trigger
        var trigger = MapStateToTrigger(equipment.State, command.TargetState);
        
        // Validate transition
        if (trigger is null || !stateMachine.CanFire(trigger.Value))
        {
            return Failure("Illegal transition");
        }

        // Fire trigger (parameterized for Fault)
        var result = trigger == EquipmentTrigger.Fault
            ? stateMachine.Fire(trigger.Value, command.Reason)
            : stateMachine.Fire(trigger.Value);

        // Save changes
        await _equipmentRepository.UpdateAsync(equipment, cancellationToken);
        
        return Success();
    }
}
```

## Equipment Entity (Domain)

The `Equipment` entity **still has its original methods** for backward compatibility:
- `Start(workOrderId)`
- `Stop()`
- `Fault(reason)`
- `EnterSetup()`
- `EnterMaintenance()`
- `Reset()`

However, **these methods are no longer used by the Application layer**. All state transitions now go through the Stateless-based state machine service.

This design allows:
- Gradual migration if needed
- Direct entity usage in tests
- Backward compatibility with any code that directly uses Equipment

## Testing the Integration

### Build Verification
```bash
dotnet build
# All projects should build successfully
```

### Unit Test Example
```csharp
[Fact]
public async Task Should_Use_Stateless_For_State_Transition()
{
    // Arrange
    var equipment = CreateTestEquipment(EquipmentState.Idle);
    var factory = new EquipmentStateMachineFactoryImpl(logger);
    var stateMachine = factory.Create(equipment);
    
    // Act
    var result = stateMachine.Fire(EquipmentTrigger.Start);
    
    // Assert
    Assert.True(result.IsSuccess);
    Assert.Equal(EquipmentState.Running, equipment.State);
}
```

### Integration Test Example
```csharp
[Fact]
public async Task Should_Transition_Via_Command_Handler()
{
    // Arrange
    var handler = CreateHandler(); // with real dependencies
    var command = new ChangeMachineStateCommand(
        EquipmentId: cnc01Id,
        TargetState: EquipmentState.Running,
        Reason: "Start production",
        TriggeredBy: "Test",
        ApprovalStatus: ApprovalStatus.Executed);
    
    // Act
    var result = await handler.HandleAsync(command);
    
    // Assert
    Assert.True(result.IsSuccess);
    Assert.Equal(EquipmentState.Running, result.NewState);
}
```

## Troubleshooting

### Factory Not Registered
**Symptom:** `InvalidOperationException: Unable to resolve service for type 'IEquipmentStateMachineFactory'`

**Solution:** Ensure factory is registered in `Program.cs`:
```csharp
builder.Services.AddSingleton<IEquipmentStateMachineFactory, EquipmentStateMachineFactoryImpl>();
```

### Invalid Trigger
**Symptom:** State machine rejects valid-looking transition

**Solution:** Check `MapStateToTrigger()` logic and ensure the trigger exists in `EquipmentStateMachineFactory` configuration

### Logging Not Working
**Symptom:** No transition logs appearing

**Solution:** Verify `ILogger<EquipmentStateMachineService>` is properly configured in DI container

## Next Steps

1. **Run the demo** - Execute the console app to verify end-to-end integration
2. **Add tests** - Unit tests for state machine, integration tests for command handlers
3. **Monitor logs** - Verify transition logging in production
4. **Extend** - Add guards, hierarchical states, or async actions as needed

## Summary

✅ **Stateless framework is now fully integrated**
- Application layer uses trigger-based API
- Command handlers fire triggers instead of calling Equipment methods
- Factory pattern maintains clean architecture
- All transitions logged and validated by Stateless
- Equipment entity kept for backward compatibility but not used by Application layer
