using System;

namespace LeitstellenPilot.Models
{
    public class UserInfo
    {
       
        public long credits_user_current { get; set; } = 0;
        public long credits_user_total { get; set; } = 0;
        public long? credits_alliance_current { get; set; }
        public long? credits_alliance_total { get; set; }
        public bool? credits_alliance_active { get; set; }
        

        public string user_name { get; set; } = "";
        public int user_id { get; set; } = 0;
        public int user_toplist_position { get; set; } = 0;
        public bool user_directplay_registered { get; set; } = false;
        public bool user_email_registered { get; set; } = false;
        public bool user_facebook_registered { get; set; } = false;
        public bool user_apple_registered { get; set; } = false;
        public int user_level { get; set; } = 0;
        public string user_level_title { get; set; } = "";
        
  
        public int coins_user_current { get; set; } = 0;
        

        public long CurrentCredits => credits_user_current;
        public long TotalCredits => credits_user_total;
        public string UserName => user_name;
        public string LevelTitle => user_level_title;
        public int ToplistPosition => user_toplist_position;
        public int Coins => coins_user_current;
        public bool HasAlliance => credits_alliance_current.HasValue;
        
  
        public long AllianceCredits => credits_alliance_current ?? 0;
        public long AllianceTotalCredits => credits_alliance_total ?? 0;
        public bool AllianceActive => credits_alliance_active ?? false;
    }
}
