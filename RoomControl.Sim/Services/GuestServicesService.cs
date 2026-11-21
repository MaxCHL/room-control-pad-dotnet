using System.Text.Json;
using RoomControl.Core.Commands;
using RoomControl.Core.Messaging;
using RoomControl.Core.Models;

namespace RoomControl.Sim.Services;

/// <summary>
/// Backend service for non-food guest requests carried on the same service/order channel as
/// food orders: housekeeping, amenity refills, wake-up calls, bill inquiries, express
/// checkout, and SOS. Each is a simple "acknowledge immediately" flow (no multi-step
/// dispatch like <see cref="RobotDispatchService"/> needs), so one service handles them all.
/// </summary>
public sealed class GuestServicesService
{
    private readonly IRoomMessageBus _bus;

    // At-most-once per (room, RequestId) for QoS1 redeliveries (see IdempotencyGate). A duplicate of a
    // completed or in-flight request returns immediately; if the in-flight attempt then fails, the
    // duplicate is retried once in the background so the request is not lost. Notifications are
    // at-least-once: a retry after a partially delivered publish can notify a subscriber twice.
    private readonly IdempotencyGate<(string RoomId, string RequestId)> _requests = new((key, ex) =>
        Console.WriteLine($"  [ERROR] GuestServicesService 重試請求 {key.RequestId} 失敗：{ex.GetBaseException().Message}"));

    public GuestServicesService(IRoomMessageBus bus)
    {
        _bus = bus;
    }

    public async Task StartAsync(CancellationToken ct = default) =>
        await _bus.SubscribeAsync(RoomTopics.ServiceOrderWildcard, OnServiceOrderAsync, ct).ConfigureAwait(false);

    private Task OnServiceOrderAsync(string topic, string payload, CancellationToken ct)
    {
        var request = JsonSerializer.Deserialize<GuestServiceRequest>(payload);
        return request is HousekeepingRequest housekeeping ? HandleAsync(housekeeping, ct) : Task.CompletedTask;
    }

    private Task HandleAsync(HousekeepingRequest request, CancellationToken ct) =>
        _requests.RunOnceAsync((request.RoomId, request.RequestId),
            work: () => ProcessAsync(request, ct),
            retry: () => HandleAsync(request, CancellationToken.None));

    private Task ProcessAsync(HousekeepingRequest request, CancellationToken ct)
    {
        var (category, message) = request.Kind switch
        {
            GuestRequestKind.Cleaning => ("Housekeeping", "已收到清潔請求，房務人員將於 20 分鐘內抵達。"),
            GuestRequestKind.AmenityRefill => ("Housekeeping", $"已收到備品補給請求（{request.Details ?? "一般備品"}），將盡快送達。"),
            GuestRequestKind.WakeUpCall => ("WakeUpCall", $"喚醒服務已設定：{request.Details ?? "明日 07:00"}。"),
            GuestRequestKind.BillInquiry => ("Billing", "目前房間帳單試算金額：NT$ 3,280（含服務費，實際以退房結帳為準）。"),
            GuestRequestKind.ExpressCheckout => ("Checkout", "快速退房已受理，帳單將寄送至您登記的 Email，祝旅途愉快。"),
            GuestRequestKind.Sos => ("Sos", "已收到緊急求助通知，櫃檯與保全人員正在前往房間。"),
            _ => ("Housekeeping", "已收到您的請求。")
        };

        var notification = new ServiceNotification
        {
            RoomId = request.RoomId,
            Category = category,
            Message = message,
            AtUtc = DateTimeOffset.UtcNow
        };

        if (request.Kind == GuestRequestKind.Sos)
        {
            Console.WriteLine($"  [告警->櫃檯/保全] 房間 {request.RoomId} 觸發 SOS 緊急求助！");
        }

        return _bus.PublishAsync(RoomTopics.ServiceNotify(request.RoomId), JsonSerializer.Serialize(notification), retain: false, ct);
    }
}
