using System.Collections.Generic;

namespace Comet.Core;

// Holds only magazines actually dropped by this bot. Combat pauses preserve the queue.
public sealed class MagazineRecovery<T> where T:class
{
    private readonly List<(string Id,T Item)> pending=new();
    public int Count=>pending.Count;
    public string Id=>pending.Count>0?pending[0].Id:"";
    public T? Item=>pending.Count>0?pending[0].Item:null;
    public bool Remember(string id,T item)
    {
        foreach(var entry in pending)if(entry.Id==id)return false;
        pending.Add((id,item));return true;
    }
    public void Finish(){if(pending.Count>0)pending.RemoveAt(0);}
    public void Clear()=>pending.Clear();
    // Losing a contact ends the tactical plan, not this bot's post-combat belongings.
    public static bool ShouldClear(string reason)=>reason.Contains("bot-ended")||reason.Contains("raid-ended")||reason.Contains("error");
    public static bool ShouldPause(string reason)=>ShouldClear(reason)||reason.Contains("ghost")||reason.Contains("inactive")||reason.Contains("authority");
    public static bool CanRecover(bool enabled,bool alive,bool authority,bool ghost,bool combat,bool nativePriority)
        =>enabled&&alive&&authority&&!ghost&&!combat&&!nativePriority;
}
