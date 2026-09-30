using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>
    /// Steps nested coroutines by hand in edit mode (no play mode, so no
    /// StartCoroutine): nested IEnumerators are pushed, web requests and other
    /// AsyncOperations are waited out a frame at a time.
    /// </summary>
    public static class EditModeCoroutines
    {
        public static IEnumerator Drive(IEnumerator root, Func<bool> tick = null)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                if (tick != null) { tick(); }
                var top = stack.Peek();
                if (!top.MoveNext()) { stack.Pop(); continue; }
                var current = top.Current;
                var nested = current as IEnumerator;
                if (nested != null) { stack.Push(nested); continue; }
                var op = current as AsyncOperation;
                if (op != null)
                {
                    while (!op.isDone)
                    {
                        if (tick != null) { tick(); }
                        yield return null;
                    }
                    continue;
                }
                yield return null;
            }
        }
    }
}
