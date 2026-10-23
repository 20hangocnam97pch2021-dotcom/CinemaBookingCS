using Microsoft.AspNetCore.Mvc;

namespace CinemaBookingCS.Controllers;

public class HomeController : Controller
{
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() => View();
}
