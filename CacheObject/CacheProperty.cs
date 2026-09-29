using UnityExplorer.Inspectors;

namespace UnityExplorer.CacheObject
{
    public class CacheProperty : CacheMember
    {
        public PropertyInfo PropertyInfo { get; internal set; }
        public override Type DeclaringType => PropertyInfo.DeclaringType;
        public override bool CanWrite => PropertyInfo.CanWrite;
        public override bool IsStatic => m_isStatic ?? (bool)(m_isStatic = PropertyInfo.GetAccessors(true)[0].IsStatic);
        private bool? m_isStatic;

        public override bool ShouldAutoEvaluate => !HasArguments;

        public CacheProperty(PropertyInfo pi)
        {
            this.PropertyInfo = pi;
        }

        public override void SetInspectorOwner(ReflectionInspector inspector, MemberInfo member)
        {
            base.SetInspectorOwner(inspector, member);

            Arguments = PropertyInfo.GetIndexParameters();
        }

        protected override object TryEvaluate()
        {
            try
            {
                object ret;
                if (HasArguments)
                    ret = PropertyInfo.GetValue(DeclaringInstance, this.Evaluator.TryParseArguments());
                else
                    ret = PropertyInfo.GetValue(DeclaringInstance, null);
                
                // Handle Il2CppSystem.Nullable<T> types when they are null
                if (ret == null && PropertyInfo.PropertyType.IsGenericType && 
                    PropertyInfo.PropertyType.FullName != null && 
                    PropertyInfo.PropertyType.FullName.StartsWith("Il2CppSystem.Nullable`1"))
                {
                    try
                    {
                        // Create a proper nullable instance instead of returning null
                        ret = Activator.CreateInstance(PropertyInfo.PropertyType);
                    }
                    catch
                    {
                        // If we can't create an instance, keep ret as null
                    }
                }
                
                LastException = null;
                return ret;
            }
            catch (TargetInvocationException tie)
            {
                // Handle TargetInvocationException specifically for Il2CppSystem.Nullable<T>
                if (tie.InnerException is NullReferenceException && 
                    PropertyInfo.PropertyType.IsGenericType && 
                    PropertyInfo.PropertyType.FullName != null && 
                    PropertyInfo.PropertyType.FullName.StartsWith("Il2CppSystem.Nullable`1"))
                {
                    try
                    {
                        // Create a proper nullable instance instead of propagating the exception
                        object ret = Activator.CreateInstance(PropertyInfo.PropertyType);
                        LastException = null;
                        return ret;
                    }
                    catch
                    {
                        // If we can't create an instance, fall through to the normal exception handling
                    }
                }
                
                LastException = tie;
                return null;
            }
            catch (Exception ex)
            {
                LastException = ex;
                return null;
            }
        }

        protected override void TrySetValue(object value)
        {
            if (!CanWrite)
                return;

            try
            {
                bool _static = PropertyInfo.GetAccessors(true)[0].IsStatic;

                if (HasArguments)
                    PropertyInfo.SetValue(DeclaringInstance, value, Evaluator.TryParseArguments());
                else
                    PropertyInfo.SetValue(DeclaringInstance, value, null);
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning(ex);
            }
        }
    }
}
