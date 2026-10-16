namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    // Tuning
    public async Task SetRecognitionTuningAsync(MapRecognitionTuning t)
    { t.ForceBestRecognitionResult = false; t.ForceCandidateSelection = false; t.PlayerDecidesScale = false; _settings!.RecognitionTuning = t; await SaveSettingsAsync(); }
    public async Task SetStructureRegistrationTuningAsync(MapStructureRegistrationTuning t)
    { _settings!.StructureRegistrationTuning = t; await SaveSettingsAsync(); }
    public async Task SetSessionTuningAsync(MapSessionTuning t)
    { _settings!.SessionTuning = t; await SaveSettingsAsync(); }
    public async Task SetFloorRecognitionTuningAsync(MapFloorRecognitionTuning t)
    { _settings!.FloorRecognitionTuning = t; await SaveSettingsAsync(); }
    public async Task SetPlayerTrackingTuningAsync(MapPlayerTrackingTuning t)
    { _settings!.PlayerTrackingTuning = t; await SaveSettingsAsync(); }

    public async Task RestoreRecognitionTuningDefaultsAsync()
    { _settings!.RecognitionTuning = new MapRecognitionTuning(); await SaveSettingsAsync(); }
    public async Task RestoreStructureRegistrationTuningDefaultsAsync()
    { _settings!.StructureRegistrationTuning = new MapStructureRegistrationTuning(); await SaveSettingsAsync(); }
    public async Task RestoreSessionTuningDefaultsAsync()
    { _settings!.SessionTuning = new MapSessionTuning(); await SaveSettingsAsync(); }
    public async Task RestoreFloorRecognitionTuningDefaultsAsync()
    { _settings!.FloorRecognitionTuning = new MapFloorRecognitionTuning(); await SaveSettingsAsync(); }
    public async Task RestorePlayerTrackingTuningDefaultsAsync()
    { _settings!.PlayerTrackingTuning = new MapPlayerTrackingTuning(); await SaveSettingsAsync(); }
}
