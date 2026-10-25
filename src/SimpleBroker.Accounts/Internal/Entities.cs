namespace SimpleBroker.Accounts.Internal;

public sealed class Account
{
    public long Id { get; set; }
    public string Owner { get; set; } = "";
    public string Currency { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public sealed class Balance
{
    public long AccountId { get; set; }
    public decimal Available { get; set; }
    public decimal Reserved { get; set; }
    public long Version { get; set; }
}

public enum ReservationStatus { Active, Released, Consumed }

public sealed class Reservation
{
    public Guid Id { get; set; }
    public long AccountId { get; set; }
    public decimal Amount { get; set; }
    public ReservationStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class Position
{
    public long AccountId { get; set; }
    public int InstrumentId { get; set; }
    public decimal Quantity { get; set; }
    public decimal AvgPrice { get; set; }
}