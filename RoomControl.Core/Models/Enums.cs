namespace RoomControl.Core.Models;

/// <summary>Kinds of in-room devices exposed on the room gateway.</summary>
public enum DeviceType
{
    Light,
    Curtain,
    Aircon,
    Tv,
    DoorSign
}

/// <summary>Motorized curtain / blackout curtain position.</summary>
public enum CurtainPosition
{
    Open,
    Closed,
    Stopped
}

/// <summary>Air-conditioner operating mode.</summary>
public enum AirconMode
{
    Cool,
    Heat,
    Fan,
    Dry,
    Auto
}

/// <summary>Electronic door sign mode (replaces the old physical door hanger).</summary>
public enum DoorSignMode
{
    None,
    DoNotDisturb,
    MakeUpRoom
}

/// <summary>Preset whole-room scenes. Each scene drives multiple devices.</summary>
public enum SceneId
{
    AllOn,
    Reading,
    Sleep,
    NightLight,
    Away,
    DoNotDisturb
}

/// <summary>Lifecycle of a food order dispatched to a delivery robot.</summary>
public enum OrderStatus
{
    Placed,
    Preparing,
    RobotAssigned,
    EnRoute,
    ArrivedAtDoor,
    Delivered,
    TimedOutReturning,
    Cancelled,
    Failed
}

/// <summary>Delivery robot state machine.</summary>
public enum RobotState
{
    Idle,
    Busy,
    EnRoute,
    WaitingForPickup,
    Returning,
    Charging,
    Fault
}

/// <summary>Restaurant walk-in queue ticket status.</summary>
public enum QueueStatus
{
    Waiting,
    Called,
    Seated,
    NoShow,
    Cancelled
}

/// <summary>Non-food guest service request kinds routed through the shared service/order channel.</summary>
public enum GuestRequestKind
{
    Cleaning,
    AmenityRefill,
    WakeUpCall,
    BillInquiry,
    ExpressCheckout,
    Sos
}
