using Server.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace Server
{
    public static class DatabaseService
    {
        public static bool RegisterUser(string username, string login, string password)
        {
            try
            {
                using (var context = new MeetingsContext())
                {
                    if (context.Users.Any(u => u.UserLogin == login))
                        return false;

                    var newUser = new User
                    {
                        UserLogin = login,
                        UserPassword = password,
                        UserName = username
                    };
                    context.Users.Add(newUser);
                    context.SaveChanges();
                    return true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                return false;
            }
        }

        public static (int?, string)? LoginUser(string login, string password)
        {
            try
            {
                using (var context = new MeetingsContext())
                {
                    var user = context.Users.FirstOrDefault(u => u.UserLogin == login && u.UserPassword == password);
                    if (user != null)
                        return (user.UserId, user.UserName);
                    return null;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                return null;
            }
        }

        public static bool CreateGuest(string guestName)
        {
            try
            {
                using (var context = new MeetingsContext())
                {
                    var guest = new Guest { GuestName = guestName };
                    context.Guests.Add(guest);
                    context.SaveChanges();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static (int? conferenceId, int? participantId) CreateConference(string username, DateTime? startDate = null)
        {
            try
            {
                using (var context = new MeetingsContext())
                {
                    var user = context.Users.FirstOrDefault(u => u.UserName == username);
                    var guest = context.Guests.FirstOrDefault(g => g.GuestName == username);

                    if (user == null && guest == null)
                        return (null, null);

                    var conference = new Conference
                    {
                        OrganizerId = user?.UserId ?? 0,
                        StartTime = startDate ?? DateTime.Now,
                        EndTime = (startDate ?? DateTime.Now).AddHours(2),
                        IsActive = true
                    };
                    context.Conferences.Add(conference);
                    context.SaveChanges();

                    if (startDate == null)
                    {
                        var participant = new ConferenceParticipant
                        {
                            ConferenceId = conference.ConferenceId,
                            UserId = user?.UserId,
                            GuestId = guest?.GuestId
                        };
                        context.ConferenceParticipants.Add(participant);
                        context.SaveChanges();
                        return (conference.ConferenceId, participant.Id);
                    }

                    return (conference.ConferenceId, null);
                }
            }
            catch
            {
                return (null, null);
            }
        }

        public static (bool success, int? participantId) JoinConference(int conferenceId, string username)
        {
            try
            {
                using (var context = new MeetingsContext())
                {
                    var conference = context.Conferences.FirstOrDefault(c => c.ConferenceId == conferenceId);
                    if (conference == null)
                        return (false, null);

                    var user = context.Users.FirstOrDefault(u => u.UserName == username);
                    var guest = context.Guests.FirstOrDefault(g => g.GuestName == username);

                    if (user == null && guest == null)
                        return (false, null);

                    bool isOrganizer = user != null && conference.OrganizerId == user.UserId;

                    if (isOrganizer)
                    {
                        if (!conference.IsActive)
                        {
                            conference.IsActive = true;
                            conference.EndTime = DateTime.Now.AddHours(2);
                            context.SaveChanges();
                        }
                    }
                    else
                    {
                        if (!context.Conferences.Any(c => c.ConferenceId == conferenceId && c.IsActive && c.StartTime <= DateTime.Now))
                            return (false, null);
                    }

                    var participant = new ConferenceParticipant
                    {
                        ConferenceId = conferenceId,
                        UserId = user?.UserId,
                        GuestId = guest?.GuestId
                    };
                    context.ConferenceParticipants.Add(participant);
                    context.SaveChanges();

                    return (true, participant.Id);
                }
            }
            catch
            {
                return (false, null);
            }
        }

        public static List<string> GetParticipants(int conferenceId)
        {
            using (var context = new MeetingsContext())
            {
                var participants = context.ConferenceParticipants
                    .Where(cp => cp.ConferenceId == conferenceId)
                    .Select(cp => new
                    {
                        Id = cp.Id,
                        Name = cp.User != null ? cp.User.UserName : cp.Guest.GuestName
                    })
                    .ToList();

                return participants.Select(p => $"{p.Id}:{p.Name}").ToList();
            }
        }

        public static int GetConferenceID(int? userId, int? guestId)
        {
            using (var context = new MeetingsContext())
            {
                return context.ConferenceParticipants
                    .Where(cp => (userId != null && cp.UserId == userId) ||
                                 (guestId != null && cp.GuestId == guestId))
                    .Select(cp => cp.ConferenceId)
                    .FirstOrDefault();
            }
        }

        public static void SaveMessageToDatabase(int senderID, int? receiverID, string messageText)
        {
            using (var context = new MeetingsContext())
            {
                var senderParticipant = context.ConferenceParticipants.FirstOrDefault(cp => cp.Id == senderID);
                if (senderParticipant == null) return;

                var message = new ConferenceMessage
                {
                    ConferenceId = senderParticipant.ConferenceId,
                    SenderId = senderID,
                    ReceiverId = receiverID,
                    MessageText = messageText,
                    SentTime = DateTime.Now
                };

                context.ConferenceMessages.Add(message);
                context.SaveChanges();
            }
        }

        public static bool ClearGuestsTable()
        {
            try
            {
                using (var context = new MeetingsContext())
                {
                    var participantsWithGuests = context.ConferenceParticipants
                        .Where(cp => cp.GuestId != null)
                        .ToList();

                    if (participantsWithGuests.Any())
                    {
                        var participantIds = participantsWithGuests.Select(cp => cp.Id).ToList();

                        Console.WriteLine($"Знайдено {participantsWithGuests.Count} учасників із GuestId. Їхні ID: {string.Join(", ", participantIds)}");

                        var messagesToDelete = context.ConferenceMessages
                            .Where(cm => participantIds.Contains(cm.SenderId) ||
                                         (cm.ReceiverId.HasValue && participantIds.Contains(cm.ReceiverId.Value)))
                            .ToList();

                        Console.WriteLine($"Знайдено {messagesToDelete.Count} повідомлень для видалення.");

                        if (messagesToDelete.Any())
                        {
                            context.ConferenceMessages.RemoveRange(messagesToDelete);
                            context.SaveChanges(); 
                        }

                        context.ConferenceParticipants.RemoveRange(participantsWithGuests);
                        context.SaveChanges(); 
                    }

                    var guests = context.Guests.ToList();
                    Console.WriteLine($"Знайдено {guests.Count} гостей для видалення.");
                    context.Guests.RemoveRange(guests);

                    context.SaveChanges();
                    Console.WriteLine("Таблиця Guests успішно очищена.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Помилка при очищенні таблиці Guests: {ex.Message}\nInner Exception: {ex.InnerException?.Message}");
                return false;
            }
        }

        public static bool ClearParticipantsTable(int conferenceId)
        {
            try
            {
                using (var context = new MeetingsContext())
                {
                    var participants = context.ConferenceParticipants
                        .Where(cp => cp.ConferenceId == conferenceId)
                        .ToList();

                    if (participants.Any())
                    {
                        var participantIds = participants.Select(cp => cp.Id).ToList();

                        Console.WriteLine($"Знайдено {participants.Count} учасників для конференції {conferenceId}. Їхні ID: {string.Join(", ", participantIds)}");

                        var messagesToDelete = context.ConferenceMessages
                            .Where(cm => participantIds.Contains(cm.SenderId) ||
                                         (cm.ReceiverId.HasValue && participantIds.Contains(cm.ReceiverId.Value)))
                            .ToList();

                        Console.WriteLine($"Знайдено {messagesToDelete.Count} повідомлень для видалення.");

                        if (messagesToDelete.Any())
                        {
                            context.ConferenceMessages.RemoveRange(messagesToDelete);
                            context.SaveChanges(); 
                        }

                        context.ConferenceParticipants.RemoveRange(participants);
                        context.SaveChanges();

                        Console.WriteLine($"Усі учасники та їхні повідомлення для конференції {conferenceId} успішно видалені.");
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Помилка при очищенні таблиці ConferenceParticipants: {ex.Message}\nInner Exception: {ex.InnerException?.Message}");
                return false;
            }
        }

        public static bool EndConference(int conferenceId)
        {
            try
            {
                using (var context = new MeetingsContext())
                {
                    var conference = context.Conferences.FirstOrDefault(c => c.ConferenceId == conferenceId);
                    if (conference != null)
                    {
                        conference.IsActive = false;
                        conference.EndTime = DateTime.Now;
                        context.SaveChanges();
                        return true;
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        public static bool DeleteOldInactiveConferences()
        {
            try
            {
                using (var context = new MeetingsContext())
                {
                    var thresholdDate = DateTime.Now.AddDays(-10);
                    var oldConferences = context.Conferences
                        .Where(c => !c.IsActive && c.EndTime < thresholdDate)
                        .ToList();

                    if (oldConferences.Any())
                    {
                        foreach (var conference in oldConferences)
                        {
                            var participants = context.ConferenceParticipants
                                .Where(cp => cp.ConferenceId == conference.ConferenceId)
                                .ToList();

                            var participantIds = participants.Select(cp => cp.Id).ToList();
                            var messages = context.ConferenceMessages
                                .Where(cm => cm.ConferenceId == conference.ConferenceId)
                                .ToList();

                            if (messages.Any())
                            {
                                context.ConferenceMessages.RemoveRange(messages);
                            }
                            if (participants.Any())
                            {
                                context.ConferenceParticipants.RemoveRange(participants);
                            }
                            context.Conferences.Remove(conference);
                        }
                        context.SaveChanges();
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                return false;
            }
        }
    }
}