using IDVBuff.Core.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace IDVBuff.Views;

// URI images share one acquisition/release contract. Unloaded also cancels a
// pending decode/download; reattaching starts a new, independently logged lease.
internal static class MemoryImageBinding
{
    private static readonly ConditionalWeakTable<Image, Binding> Bindings = new();
    private static readonly ConditionalWeakTable<FrameworkElement, List<Image>> OwnedImages = new();

    internal static void Attach(Image image, Uri uri, int decodePixelWidth, string resource,
        Action<BitmapImage>? opened = null) =>
        AttachCore(image, uri, decodePixelWidth, resource, opened, BitmapCreateOptions.IgnoreImageCache, false);

    private static void AttachCore(Image image, Uri uri, int decodePixelWidth, string resource,
        Action<BitmapImage>? opened, BitmapCreateOptions createOptions, bool loadImmediately)
    {
        Release(image);
        var binding = new Binding(image, uri, decodePixelWidth, resource, opened, createOptions, loadImmediately);
        Bindings.Add(image, binding);
        binding.Attach();
    }

    // Page-owned packaged artwork keeps its original eager decode/cache policy.
    // The shell retires these consumers while their content root still exists.
    internal static void AttachOwned(FrameworkElement owner, Image image, Uri uri, string resource)
    {
        AttachCore(image, uri, 0, resource, null, BitmapCreateOptions.None, true);
        OwnedImages.GetOrCreateValue(owner).Add(image);
    }

    internal static void ReleaseOwned(FrameworkElement owner)
    {
        if (!OwnedImages.TryGetValue(owner, out var images)) return;
        foreach (var image in images) Release(image);
        images.Clear();
        OwnedImages.Remove(owner);
    }

    internal static void Release(Image image)
    {
        if (!Bindings.TryGetValue(image, out var binding)) return;
        binding.Detach();
        Bindings.Remove(image);
    }

    private sealed class Binding(Image image, Uri uri, int decodePixelWidth, string resource,
        Action<BitmapImage>? opened, BitmapCreateOptions createOptions, bool loadImmediately)
    {
        private BitmapImage? _bitmap;

        internal void Attach()
        {
            var mounted = image.IsLoaded;
            image.Loaded += Loaded;
            image.Unloaded += Unloaded;
            MemoryJournal.Current?.Record("image-binding-attached", new { resource, mounted, mountedAfterSubscription = image.IsLoaded, decodePixelWidth });
            if (mounted || loadImmediately) Load();
        }

        internal void Detach()
        {
            image.Loaded -= Loaded;
            image.Unloaded -= Unloaded;
            Unload();
        }

        private void Loaded(object sender, RoutedEventArgs args) => Load();
        private void Unloaded(object sender, RoutedEventArgs args) => Unload();

        private void Load()
        {
            if (_bitmap is not null) return;
            var request = MemoryJournal.Request(resource, nativeModule: "Microsoft.UI.Xaml.dll");
            var bitmap = _bitmap = new BitmapImage { DecodePixelWidth = decodePixelWidth,
                CreateOptions = createOptions };
            MemoryJournal.TrackResource(bitmap, resource, null, "Microsoft.UI.Xaml.dll", request);
            MemoryJournal.ObserveManagedObject(bitmap, resource);
            bitmap.ImageOpened += ImageOpened;
            image.Source = bitmap;
            bitmap.UriSource = uri;
        }

        private void ImageOpened(object sender, RoutedEventArgs args)
        {
            if (sender is not BitmapImage decoded || !ReferenceEquals(decoded, _bitmap)) return;
            MemoryJournal.Current?.Record("image-decoded",
                new { resource, decoded.PixelWidth, decoded.PixelHeight, decoded.DecodePixelWidth,
                    meaning = "decoded image dimensions; native payload not measured" });
            opened?.Invoke(decoded);
        }

        private void Unload()
        {
            var released = _bitmap;
            _bitmap = null;
            if (released is null) return;
            released.ImageOpened -= ImageOpened;
            // WinUI unregisters URI images through their current content root.
            // Clear the URI before severing the Image -> BitmapImage association.
            released.UriSource = null;
            if (ReferenceEquals(image.Source, released)) image.Source = null;
            MemoryJournal.ReleaseResource(released);
        }
    }
}
