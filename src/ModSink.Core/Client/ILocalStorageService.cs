﻿using System;
using System.IO;
using System.Threading.Tasks;
using ModSink.Core.Models.Repo;

namespace ModSink.Core.Client
{
    public interface ILocalStorageService
    {
        Task Delete(FileSignature fileSignature);

        Task<FileInfo> GetFileInfo(FileSignature fileSignature);

        string GetFileName(FileSignature fileSignature);

        Uri GetFileUri(FileSignature fileSignature);

        Task<bool> IsFileAvailable(FileSignature fileSignature);

        Task<Stream> Read(FileSignature fileSignature);

        Task<Stream> Write(FileSignature fileSignature);

        Task<(bool available, Lazy<Task<Stream>> stream)> WriteIfMissingOrInvalid(FileSignature fileSignature);
    }
}