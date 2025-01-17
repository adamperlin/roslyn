﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host;

namespace Microsoft.CodeAnalysis.TodoComments
{
    /// <summary>
    /// Determines locations of 'todo' comments within a particular file.  The specific 'todo' comment forms (e.g.
    /// '<c>TODO</c>', '<c>UNDONE</c>', etc.) are provided through <see cref="TodoCommentDescriptor"/>.
    /// </summary>
    internal interface ITodoCommentDataService : ILanguageService
    {
        Task<ImmutableArray<TodoCommentData>> GetTodoCommentDataAsync(Document document, ImmutableArray<TodoCommentDescriptor> commentDescriptors, CancellationToken cancellationToken);
    }
}
