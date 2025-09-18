using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Markowitz.Core.Models;

namespace Markowitz.Web;

public static class EnumExtensions
{
    public static string GetDisplayName(this OptimizationTarget target)
    {
        var member = typeof(OptimizationTarget).GetMember(target.ToString()).FirstOrDefault();
        if (member is null)
            return target.ToString();

        var attr = member.GetCustomAttribute<DisplayAttribute>();
        return attr?.GetName() ?? target.ToString();
    }
}
