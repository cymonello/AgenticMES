using AgenticMES.Application.DTOs;
using AgenticMES.Domain.Entities;

namespace AgenticMES.Application.Common.Interfaces;

/// <summary>
/// Computes ISO 22400 Overall Equipment Effectiveness (Availability × Performance × Quality)
/// from ISA-95 equipment state and work-order production parameters.
/// </summary>
public interface IOeeCalculatorService
{
    /// <summary>
    /// Calculates OEE for the given equipment and the work order dispatched to it.
    /// Optional <paramref name="parameters"/> supply scrap, downtime, and ideal cycle time
    /// when those facts are not present on the aggregates.
    /// </summary>
    OeeCalculationResult Calculate(
        Equipment equipment,
        WorkOrder workOrder,
        OeeCalculationParameters? parameters = null) =>
        Calculate(OeeCalculationRequest.From(equipment, workOrder, parameters));

    /// <summary>Calculates OEE from a flattened equipment / work-order snapshot.</summary>
    OeeCalculationResult Calculate(OeeCalculationRequest request);
}
