namespace QOVETER.Models
{
    public class LevelInfo
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public double Elevation { get; set; }
        public int FloorNumber { get; set; }

        public override string ToString() => Name;
    }
}