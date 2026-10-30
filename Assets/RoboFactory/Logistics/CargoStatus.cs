namespace RoboFactory.Logistics
{
    /// <summary>
    /// Logistics stage of a box. The status is NOT stored on the box:
    /// it is deduced from its hierarchy (under a pallet, a bench, a robot, the truck),
    /// which avoids any desync between the visual world and the logic.
    /// </summary>
    public enum CargoStatus
    {
        Unknown,
        OnPallet,   // under a level point of a pallet
        OnBench,    // under a level point of a bench
        Carried,    // under a robot (whichever one)
        InTruck,    // under a TruckBay slot
        Loose       // nowhere else (on the floor, moved by hand...)
    }

    public static class CargoStatusNames
    {
        public static string Of(CargoStatus status)
        {
            switch (status)
            {
                case CargoStatus.OnPallet: return "pallet";
                case CargoStatus.OnBench: return "bench";
                case CargoStatus.Carried: return "carried";
                case CargoStatus.InTruck: return "truck";
                case CargoStatus.Loose: return "on the floor";
                default: return "unknown";
            }
        }
    }
}
