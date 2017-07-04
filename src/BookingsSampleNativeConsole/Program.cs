﻿// ---------------------------------------------------------------------------
// <copyright file="Program.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
// ---------------------------------------------------------------------------

namespace BookingsSampleNativeConsole
{
    using System;
    using System.Diagnostics;

    using Microsoft.Bookings.Client;
    using Microsoft.IdentityModel.Clients.ActiveDirectory;
    using Microsoft.OData.Client;
    using System.Linq;
    using System.Net;

    public class Program
    {
        // See README.MD for instructions on how to get your own values for these two settings.
        // See also https://docs.microsoft.com/en-us/azure/active-directory/develop/active-directory-authentication-scenarios#native-application-to-web-api
        private static string clientApplicationAppId;
        private static Uri clientApplicationRedirectUri;

        public static void Main()
        {
            try
            {
                if (clientApplicationAppId == null)
                {
                    Console.Write("Client Application AppId      : ");
                    clientApplicationAppId = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(clientApplicationAppId))
                    {
                        Console.WriteLine("Update sample to include your own client application ID");
                        return;
                    }
                }

                if (clientApplicationRedirectUri == null)
                {
                    Console.Write("Client Application RedirectUri: ");
                    var uri = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(uri))
                    {
                        Console.WriteLine("Update sample to include your own client application redirect uri");
                        return;
                    }
                    clientApplicationRedirectUri = new Uri(uri);
                }

                // ADAL: https://docs.microsoft.com/en-us/azure/active-directory/develop/active-directory-authentication-libraries
                var authenticationContext = new AuthenticationContext(BookingsContainer.DefaultAadInstance, TokenCache.DefaultShared);
                var authenticationResult = authenticationContext.AcquireTokenAsync(
                    BookingsContainer.ResourceId,
                    clientApplicationAppId,
                    clientApplicationRedirectUri,
                    new PlatformParameters(PromptBehavior.RefreshSession)).Result;

                // This BookingsContainer is generated by the ODATA v4 Client Code Generator
                // See https://odata.github.io and https://github.com/odata/odata.net for usage.
                // Note that the code generator customizes the entity and property names to PascalCase
                // so they match C# guidelines, while the EDM uses lower case camelCase, as per Graph guidelies.
                // Since the application is short lived, the delegate is simply returning the authorization
                // header obtained above; a long lived application would likely need to refresh the token
                // when it expires, so it would have a slightly more complex delegate.
                var bookingsContainer = new BookingsContainer(
                    BookingsContainer.DefaultV1ServiceRoot,
                    () => authenticationResult.CreateAuthorizationHeader());

                // Fiddler makes it easy to look at the request/response payloads. Use it automatically if it is running.
                // https://www.telerik.com/download/fiddler
                if (Process.GetProcessesByName("fiddler").Any())
                {
                    bookingsContainer.WebProxy = new WebProxy(new Uri("http://localhost:8888"), false);
                }

                // Get the list of booking businesses that the logged on user can see.
                // NOTE: I'm not using 'async' in this sample for simplicity;
                // the ODATA client library has full support for async invocations.
                var bookingBusinesses = bookingsContainer.BookingBusinesses.ToArray();
                foreach (var _ in bookingBusinesses)
                {
                    Console.WriteLine(_.DisplayName);
                }

                if (bookingBusinesses.Length == 0)
                {
                    Console.WriteLine("Enter a name for a new booking business, or leave empty to exit.");
                }
                else
                {
                    Console.WriteLine("Type the name of the booking business to use or enter a new name to create a new booking business, or leave empty to exit.");
                }

                var businessName = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(businessName))
                {
                    return;
                }

                // See if the name matches one of the entities we have (this is searching the local array)
                var bookingBusiness = bookingBusinesses.FirstOrDefault(_ => _.DisplayName == businessName);
                if (bookingBusiness == null)
                {
                    // If we don't have a match, create a new bookingBusiness.
                    // All we need to pass is the display name, but we could pass other properties if needed.
                    // This NewEntityWithChangeTracking is a custom extension to the standard ODATA library to make it easy.
                    // Keep in mind there are other patterns that could be used, revolving around DataServiceCollection.
                    // The trick is that the data object must be tracked by a DataServiceCollection and then we need
                    // to save with SaveChangesOptions.PostOnlySetProperties.
                    bookingBusiness = bookingsContainer.BookingBusinesses.NewEntityWithChangeTracking();
                    bookingBusiness.DisplayName = businessName;
                    Console.WriteLine("Creating new booking business...");
                    bookingsContainer.SaveChanges(SaveChangesOptions.PostOnlySetProperties);

                    Console.WriteLine($"Booking Business Created: {bookingBusiness.Id}. Press any key to continue.");
                    Console.ReadKey();
                }
                else
                { 
                    Console.WriteLine("Using existing booking business.");  
                }

                // Play with the newly minted booking business
                var business = bookingsContainer.BookingBusinesses.ByKey(bookingBusiness.Id);

                // Add an external staff member (these are easy, as we don't need to find another user in the AD).
                // For an internal staff member, the application might query the user or the Graph to find other users.
                var staff = business.StaffMembers.FirstOrDefault();
                if (staff == null)
                {
                    staff = business.StaffMembers.NewEntityWithChangeTracking();
                    staff.EmailAddress = "staff1@contoso.com";
                    staff.DisplayName = "Staff1";
                    staff.Role = BookingStaffRole.ExternalGuest;
                    Console.WriteLine("Creating staff member...");
                    bookingsContainer.SaveChanges(SaveChangesOptions.PostOnlySetProperties);
                    Console.WriteLine("Staff created.");
                }
                else
                {
                    Console.WriteLine($"Using staff member {staff.DisplayName}");
                }

                // Add an Appointment
                var newAppointment = business.Appointments.NewEntityWithChangeTracking();
                newAppointment.CustomerEmailAddress = "customer@contoso.com";
                newAppointment.CustomerName = "John Doe";
                newAppointment.ServiceId = business.Services.First().Id; // assuming we didn't deleted all services; we might want to double check first like we did with staff.
                newAppointment.StaffMemberIds.Add(staff.Id);
                newAppointment.Reminders.Add(new BookingReminder { Message = "Hello", Offset = TimeSpan.FromHours(1), Recipients = BookingReminderRecipients.AllAttendees });
                var start = DateTime.Today.AddDays(1).AddHours(13).ToUniversalTime();
                var end = start.AddHours(1);
                newAppointment.Start = new DateTimeTimeZone { DateTime = start.ToString("o"), TimeZone = "UTC" };
                newAppointment.End = new DateTimeTimeZone { DateTime = end.ToString("o"), TimeZone = "UTC" };
                Console.WriteLine("Creating appointment...");
                bookingsContainer.SaveChanges(SaveChangesOptions.PostOnlySetProperties);
                Console.WriteLine("Appointment created.");

                // Query appointments.
                // Note: the server imposes a limit on the number of appointments returned in each request
                // so clients must use paging or request a calendar view with business.GetCalendarView().
                foreach (var appointment in business.Appointments.GetAllPages())
                {
                    // DateTimeTimeZone comes from Graph and it uses string for the DateTime, not sure why.
                    // Perhaps we could tweak the generated proxy (or add extension method) to automatically 
                    // do this ToString/Parse for us, so it does not pollute the entire code.
                    Console.WriteLine($"{DateTime.Parse(appointment.Start.DateTime).ToLocalTime()}: {appointment.ServiceName} with {appointment.CustomerName}");
                }

                // In order for customers to interact with the booking business we need to publish its public page.
                // We can also Unpublish() to hide it from customers, but where is the fun in that?
                Console.WriteLine("Publishing booking business public page...");
                business.Publish().Execute();

                // Let the user play with the public page
                Console.WriteLine(business.GetValue().PublicUrl);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }

            Console.WriteLine("Done. Press any key to exit.");
            Console.ReadKey();
        }
    }
}