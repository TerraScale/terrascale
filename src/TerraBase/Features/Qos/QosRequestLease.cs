namespace TerraBase.Features.Qos;

/// <summary>
/// The request's slot in its organization's concurrent request limit. A long-lived stream releases it once it starts
/// streaming, so open streams do not lock the organization out of other requests; the stream's own limits bound it instead.
/// </summary>
/// <param name="Lease">Released at most once, however often it is disposed.</param>
public sealed record QosRequestLease(IQosLease Lease);
