﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Remote;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.TodoComments
{
    internal abstract class AbstractTodoCommentService : ITodoCommentDataService
    {
        protected abstract bool PreprocessorHasComment(SyntaxTrivia trivia);
        protected abstract bool IsSingleLineComment(SyntaxTrivia trivia);
        protected abstract bool IsMultilineComment(SyntaxTrivia trivia);
        protected abstract bool IsIdentifierCharacter(char ch);

        protected abstract string GetNormalizedText(string message);
        protected abstract int GetCommentStartingIndex(string message);
        protected abstract void AppendTodoComments(ImmutableArray<TodoCommentDescriptor> commentDescriptors, SyntacticDocument document, SyntaxTrivia trivia, ArrayBuilder<TodoCommentData> todoList);

        public async Task<ImmutableArray<TodoCommentData>> GetTodoCommentDataAsync(
            Document document,
            ImmutableArray<TodoCommentDescriptor> commentDescriptors,
            CancellationToken cancellationToken)
        {
            var client = await RemoteHostClient.TryGetClientAsync(document.Project, cancellationToken).ConfigureAwait(false);
            if (client != null)
            {
                var result = await client.TryInvokeAsync<IRemoteTodoCommentsDiscoveryService, ImmutableArray<TodoCommentData>>(
                    document.Project,
                    (service, checksum, cancellationToken) => service.GetTodoCommentDataAsync(checksum, document.Id, commentDescriptors, cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                if (!result.HasValue)
                    return ImmutableArray<TodoCommentData>.Empty;

                return result.Value;
            }

            return await GetTodoCommentDataInProcessAsync(document, commentDescriptors, cancellationToken).ConfigureAwait(false);
        }

        private async Task<ImmutableArray<TodoCommentData>> GetTodoCommentDataInProcessAsync(
            Document document,
            ImmutableArray<TodoCommentDescriptor> commentDescriptors,
            CancellationToken cancellationToken)
        {
            if (commentDescriptors.IsEmpty)
                return ImmutableArray<TodoCommentData>.Empty;

            cancellationToken.ThrowIfCancellationRequested();

            // strongly hold onto text and tree
            var syntaxDoc = await SyntacticDocument.CreateAsync(document, cancellationToken).ConfigureAwait(false);

            // reuse list
            using var _ = ArrayBuilder<TodoCommentData>.GetInstance(out var todoList);

            foreach (var trivia in syntaxDoc.Root.DescendantTrivia())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!ContainsComments(trivia))
                    continue;

                AppendTodoComments(commentDescriptors, syntaxDoc, trivia, todoList);
            }

            return todoList.ToImmutable();
        }

        private bool ContainsComments(SyntaxTrivia trivia)
            => PreprocessorHasComment(trivia) || IsSingleLineComment(trivia) || IsMultilineComment(trivia);

        protected void AppendTodoCommentInfoFromSingleLine(
            ImmutableArray<TodoCommentDescriptor> commentDescriptors,
            SyntacticDocument document,
            string message, int start,
            ArrayBuilder<TodoCommentData> todoList)
        {
            var index = GetCommentStartingIndex(message);
            if (index >= message.Length)
            {
                return;
            }

            var normalized = GetNormalizedText(message);
            foreach (var commentDescriptor in commentDescriptors)
            {
                var token = commentDescriptor.Text;
                if (string.Compare(
                        normalized, index, token, indexB: 0,
                        length: token.Length, comparisonType: StringComparison.OrdinalIgnoreCase) != 0)
                {
                    continue;
                }

                if ((message.Length > index + token.Length) && IsIdentifierCharacter(message[index + token.Length]))
                {
                    // they wrote something like:
                    // todoboo
                    // instead of
                    // todo
                    continue;
                }

                var trimmedMessage = message[index..];
                var position = start + index;

                // Go through SyntaxTree so that any `#line` remapping is picked up
                var location = document.SyntaxTree.GetLocation(new TextSpan(position, 0));

                todoList.Add(new TodoCommentData(
                    commentDescriptor.Priority, trimmedMessage, document.Document.Id, location.GetLineSpan(), location.GetMappedLineSpan()));
            }
        }

        protected void ProcessMultilineComment(
            ImmutableArray<TodoCommentDescriptor> commentDescriptors,
            SyntacticDocument document,
            SyntaxTrivia trivia, int postfixLength,
            ArrayBuilder<TodoCommentData> todoList)
        {
            // this is okay since we know it is already alive
            var text = document.Text;

            var fullSpan = trivia.FullSpan;
            var fullString = trivia.ToFullString();

            var startLine = text.Lines.GetLineFromPosition(fullSpan.Start);
            var endLine = text.Lines.GetLineFromPosition(fullSpan.End);

            // single line multiline comments
            if (startLine.LineNumber == endLine.LineNumber)
            {
                var message = postfixLength == 0 ? fullString : fullString.Substring(0, fullSpan.Length - postfixLength);
                AppendTodoCommentInfoFromSingleLine(commentDescriptors, document, message, fullSpan.Start, todoList);
                return;
            }

            // multiline 
            var startMessage = text.ToString(TextSpan.FromBounds(fullSpan.Start, startLine.End));
            AppendTodoCommentInfoFromSingleLine(commentDescriptors, document, startMessage, fullSpan.Start, todoList);

            for (var lineNumber = startLine.LineNumber + 1; lineNumber < endLine.LineNumber; lineNumber++)
            {
                var line = text.Lines[lineNumber];
                var message = line.ToString();

                AppendTodoCommentInfoFromSingleLine(commentDescriptors, document, message, line.Start, todoList);
            }

            var length = fullSpan.End - endLine.Start;
            if (length >= postfixLength)
            {
                length -= postfixLength;
            }

            var endMessage = text.ToString(new TextSpan(endLine.Start, length));
            AppendTodoCommentInfoFromSingleLine(commentDescriptors, document, endMessage, endLine.Start, todoList);
        }
    }
}
