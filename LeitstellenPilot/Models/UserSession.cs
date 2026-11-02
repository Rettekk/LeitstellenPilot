using System;

namespace LeitstellenPilot.Models
{
    public class UserSession
    {
        public string UserName { get; set; } = "";
        public DateTime LastLogin { get; set; } = DateTime.MinValue;
        public string SessionId { get; set; } = "";
        public string Cookies { get; set; } = "";
        public string AuthToken { get; set; } = "";
        public string LoginMethod { get; set; } = "";
        public string UserId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
        public int SessionDurationHours { get; set; } = 6;
        public bool HasStoredSession => !string.IsNullOrEmpty(SessionId) && !string.IsNullOrEmpty(UserName);
        public bool IsValid => 
            !string.IsNullOrEmpty(SessionId) &&
            !string.IsNullOrEmpty(UserName) && 
            LastLogin > DateTime.MinValue &&
            LastLogin.AddHours(SessionDurationHours) > DateTime.UtcNow;
        
        public bool IsExpired => !IsValid;
        
        public bool HasValidSession => 
            !string.IsNullOrEmpty(SessionId) && 
            !string.IsNullOrEmpty(UserName) && 
            IsValid;
            
        public bool HasAuthToken => !string.IsNullOrEmpty(AuthToken);
        
        // Helper Methods
        public DateTime ExpiresAt => LastLogin.AddHours(SessionDurationHours);
        
        public TimeSpan TimeUntilExpiry => ExpiresAt - DateTime.UtcNow;
        
        public bool WillExpireSoon(int minutesThreshold = 30) => 
            TimeUntilExpiry.TotalMinutes <= minutesThreshold && TimeUntilExpiry.TotalMinutes > 0;
            
        public void UpdateActivity()
        {
            LastActivity = DateTime.UtcNow;
        }
        
        public override string ToString()
        {
            return $"Session[{UserName}] - Valid: {IsValid}, Expires: {ExpiresAt:yyyy-MM-dd HH:mm:ss}";
        }
    }
}
