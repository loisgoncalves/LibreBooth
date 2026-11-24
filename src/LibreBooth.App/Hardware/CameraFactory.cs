using LibreBooth.App.Hardware.Canon;
using LibreBooth.App.Hardware.Demo;
using LibreBooth.Core.Config;
using LibreBooth.Core.Hardware;

namespace LibreBooth.App.Hardware;

/// <summary>
/// L'appareil photo choisi dans les réglages : l'appareil de démonstration, ou le Canon piloté par le pilote de
/// Windows (par défaut depuis la 3.1.0) ou par le SDK Canon (solution de secours).
/// </summary>
internal static class CameraFactory
{
    public static ICamera Create(Func<CameraSettings> settings, bool demo) =>
        demo || settings().Kind == CameraKind.Demo ? new DemoCamera() : CreateCanon(settings);

    /// <summary>Le Canon avec le pilote choisi, même si l'appareil de démonstration est choisi (vérifications du matériel).</summary>
    public static ICamera CreateCanon(Func<CameraSettings> settings) =>
        settings().Driver == CameraDriver.CanonSdk ? new CanonCamera(settings) : new CanonWindowsCamera(settings);

    /// <summary>Type de l'appareil que <see cref="Create"/> donnerait : pour savoir s'il faut remplacer l'appareil en place.</summary>
    public static Type TypeFor(CameraSettings settings, bool demo) =>
        demo || settings.Kind == CameraKind.Demo ? typeof(DemoCamera)
        : settings.Driver == CameraDriver.CanonSdk ? typeof(CanonCamera)
        : typeof(CanonWindowsCamera);
}
