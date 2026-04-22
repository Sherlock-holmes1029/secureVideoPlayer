using System;

namespace WpfApp1
{
    public class VideoNote
    {
        public double Timestamp { get; set; }
        public string Text { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public string TimestampLabel
        {
            get
            {
                var t = TimeSpan.FromSeconds(Timestamp);
                return t.TotalHours >= 1
                    ? t.ToString(@"h\:mm\:ss")
                    : t.ToString(@"m\:ss");
            }
        }
    }
}
