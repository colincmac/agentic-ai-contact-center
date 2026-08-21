namespace Agents.AI.Extensions.SensitiveData;

[AttributeUsage(AttributeTargets.Parameter)]
public class SensitiveParameterAttribute : Attribute
{
    public string ContextSource { get; set; } = "";
    public bool RequiresDecryption { get; set; }
}
