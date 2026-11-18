using UniversalMeetingRecorder.Core.Meetings;
using Microsoft.VisualBasic.FileIO;

namespace UniversalMeetingRecorder.App.Platform;

/// <summary>Папка встречи уезжает в Корзину, а не стирается: запись может быть единственной копией разговора.</summary>
public sealed class RecycleBin : IRecycleBin
{
    public void MoveToRecycleBin(string directoryPath) =>
        FileSystem.DeleteDirectory(directoryPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
}
