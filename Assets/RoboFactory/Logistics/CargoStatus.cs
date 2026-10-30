namespace RoboFactory.Logistics
{
    /// <summary>
    /// Étape logistique d'un carton. Le statut n'est PAS stocké sur le carton :
    /// il est déduit de sa hiérarchie (sous une palette, un banc, un robot, le camion),
    /// ce qui évite toute désynchronisation entre le monde visuel et la logique.
    /// </summary>
    public enum CargoStatus
    {
        Unknown,
        OnPallet,   // sous un point de niveau d'une palette
        OnBench,    // sous un point de niveau d'un banc
        Carried,    // sous un robot (peu importe lequel)
        InTruck,    // sous un slot du TruckBay
        Loose       // nulle part ailleurs (au sol, déplacé à la main...)
    }

    public static class CargoStatusNames
    {
        public static string Of(CargoStatus status)
        {
            switch (status)
            {
                case CargoStatus.OnPallet: return "palette";
                case CargoStatus.OnBench: return "banc";
                case CargoStatus.Carried: return "transporté";
                case CargoStatus.InTruck: return "camion";
                case CargoStatus.Loose: return "au sol";
                default: return "inconnu";
            }
        }
    }
}
