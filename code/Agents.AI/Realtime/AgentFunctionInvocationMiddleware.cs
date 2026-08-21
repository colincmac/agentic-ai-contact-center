using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agents.AI.Realtime;

public delegate ValueTask<object?> AgentFunctionInvocationMiddleware(
    AIAgent agent,
    AIFunctionArguments arguments,
    AIFunction function,
    Func<AIFunctionArguments, CancellationToken, ValueTask<object?>> next,
    CancellationToken cancellationToken);
