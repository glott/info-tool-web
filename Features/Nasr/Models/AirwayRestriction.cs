namespace ZoaReference.Features.Nasr.Models;

/// <summary>
/// MEA/MOCA altitude restriction from a NASR AWY1 record. Altitudes are in
/// feet. <paramref name="Sequence"/> links to <see cref="AirwayFix.Sequence"/>:
/// the restriction covers the segment ending at the fix with this sequence.
/// </summary>
public record AirwayRestriction(
    string AirwayId,
    int Sequence,
    int? Mea,
    int? MeaOpposite,
    int? Moca);
