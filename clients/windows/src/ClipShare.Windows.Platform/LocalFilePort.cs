using ClipShare.Windows.Application;

namespace ClipShare.Windows.Platform;

/// <summary>把用户显式选择的路径转换为受约束的流端口。</summary>
public sealed class LocalFilePort : ILocalFilePort
{
    public IUploadFile OpenUpload(string userSelectedPath) => new LocalUploadFile(userSelectedPath);

    public IDownloadTarget CreateDownloadTarget(string userSelectedPath) =>
        new AtomicDownloadTarget(userSelectedPath);
}
