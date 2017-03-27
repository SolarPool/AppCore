using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Disposables;
using Autofac;
using Ciphernote.UI;
using FluentValidation;
using FluentValidation.Results;
using ReactiveUI;

namespace Ciphernote.ViewModels
{
    public abstract class ViewModelBase : ReactiveObject,
		INotifyDataErrorInfo,
        IDisposable
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ViewModelBase"/> class.
        /// </summary>
        protected ViewModelBase(IComponentContext ctx)
        {
            this.ctx = ctx;
            dispatcher = ctx.Resolve<IDispatcher>();
        }

        public virtual void Dispose()
        {
            disposables?.Dispose();
            disposables = null;
        }

        #region Members

        protected CompositeDisposable disposables = new CompositeDisposable();

        #endregion
      
        #region Properties

        protected IDispatcher dispatcher;

        /// <summary>
        /// Returns the user-friendly name of this object.
        /// Child classes can set this property to a new value,
        /// or override it to determine the value on-demand.
        /// </summary>
        public virtual string DisplayName { get; protected set; }
    
		#endregion

        #region IDataErrorInfo implementation
#if !NETFX_CORE

		/// <summary>
		/// Gets an error message indicating what is wrong with this object.
		/// </summary>
		public string Error { get; private set; }
#endif

        IList<ValidationFailure> validationErrors;
        protected IComponentContext ctx;

        /// <summary>
		/// Gets or sets the current validation error collection
		/// </summary>
		public IList<ValidationFailure> ValidationErrors
		{
			get { return validationErrors; }
			protected set
			{
				List<string> obsoleteValidationErrors = null;

				// collect names of properties that do not longer have errors
				if (ErrorsChanged != null)
				{
					var oldErrorsCollection = ValidationErrors != null && ValidationErrors.Count > 0 ? ValidationErrors : new List<ValidationFailure>();
					var newErrorsCollection = value != null && value.Count > 0 ? value : new List<ValidationFailure>();
					var newPropertyNames = newErrorsCollection.Select(x => x.PropertyName).Distinct().ToDictionary(x => x);

					// figure out which errors are no longer part of the new validation error collection
					obsoleteValidationErrors = oldErrorsCollection.Where(x =>
			         !newPropertyNames.ContainsKey(x.PropertyName)).Select(x => x.PropertyName).Distinct().ToList();
				}

				if (!EqualityComparer<IList<ValidationFailure>>.Default.Equals(validationErrors, value))
				{
                    this.RaiseAndSetIfChanged(ref validationErrors, value);

					// fire event for properties that do not longer have errors
					if (obsoleteValidationErrors != null)
					{
						foreach (var obsoleteValidationErrorPropertyName in obsoleteValidationErrors)
							ErrorsChanged(this, new DataErrorsChangedEventArgs(obsoleteValidationErrorPropertyName));
					}

					// fire event for properties that now have errors
					if (value != null && ErrorsChanged != null)
					{
						var propertyNames = value.Select(x => x.PropertyName).Distinct().ToList();

						foreach (var failedProperty in propertyNames)
							ErrorsChanged(this, new DataErrorsChangedEventArgs(failedProperty));
					}
				}
			}
		}

		/// <summary>
		/// Gets a value indicating whether the ViewModel has validation errors
		/// </summary>
		/// <value>
		///     <c>true</c> if this instance has validation errors; otherwise, <c>false</c>.
		/// </value>
		public bool HasValidationErrors => (this.ValidationErrors != null) && (this.ValidationErrors.Count > 0);

        /// <summary>
		/// Gets the error message for the property with the given name.
		/// </summary>
		/// <param name="propertyName">The name of the property whose error message to get.</param>
		/// <returns>The error message for the property. The default is an empty string ("").</returns>
		public virtual string this[string propertyName]
		{
			get
			{
				return this.ValidationErrors != null && this.ValidationErrors.Any(error => error.PropertyName == propertyName)
					? string.Join(Environment.NewLine, (from error in this.ValidationErrors where error.PropertyName == propertyName select error.ErrorMessage).ToArray())
						: null;
			}
		}


		#endregion

		#region INotifyDataErrorInfo implementation

		/// <summary>
		/// Gets the validation errors for a specified property or for the entire object.
		/// </summary>
		/// <param name="propertyName">The name of the property to retrieve validation errors for, or null or <see cref="F:System.String.Empty"/> to retrieve errors for the entire object.</param>
		/// <returns>
		/// The validation errors for the property or object.
		/// </returns>
		public IEnumerable GetErrors(string propertyName)
		{
			if (HasErrors)
			{
				if (string.IsNullOrEmpty(propertyName))
					return ValidationErrors;

				return ValidationErrors
					.Where(x => x.PropertyName == propertyName)
					.ToList();
			}

			return null;
		}

		/// <summary>
		/// Gets a value that indicates whether the object has validation errors.
		/// </summary>
		/// <value></value>
		/// <returns>true if the object currently has validation errors; otherwise, false.</returns>
		public bool HasErrors => ValidationErrors != null && ValidationErrors.Count > 0;

        /// <summary>
		/// Occurs when the validation errors have changed for a property or for the entire object.
		/// </summary>
		public event EventHandler<DataErrorsChangedEventArgs> ErrorsChanged;

		#endregion

		/// <summary>
		/// Validates the model using specified validator
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="validator">The validator.</param>
		/// <returns></returns>
		public bool Validate<T>(AbstractValidator<T> validator) where T : ViewModelBase
		{
			var result = validator.Validate((T) this);
			ValidationErrors = result.Errors;
			return result.IsValid;
		}
    }
}