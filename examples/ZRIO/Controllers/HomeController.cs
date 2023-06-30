﻿using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Imageflow.Net.Server.Example.Models;
using System.Linq;
using Imageflow.Server.Storage.RemoteReader;

namespace Imageflow.Net.Server.Example.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;

        public HomeController(ILogger<HomeController> logger)
        {
            _logger = logger;
        }

        public IActionResult Index()
        {
            return View();
        }

        public IActionResult Privacy()
        {
            return View();
        }

        public IActionResult Gallery()
        {
            int[] imageNumbers = new int[] { 3, 4, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23 };

            var imageUrls = 
                imageNumbers.Select(i => $"/ri/{i}s.jpg")
                .Concat(imageNumbers.Select(i => $"/ri/{i}.jpg"))
                .ToList();

            var remoteUrls = imageNumbers
                .Select(i => $"http://{Request.Host}/ri/{i}.jpg")
                .Select(u => $"/remote/{RemoteReaderService.EncodeAndSignUrl(u, "ChangeMe")}").ToList();

            return View(imageUrls.Concat(remoteUrls).ToList());
        }
        
        public IActionResult LoadTest()
        {
            var imageUrl = "/images/fire-umbrella-small.jpg?width=300&height=300&mode=pad";

            var imageUrls = new List<string>(1000);

            for (var j = 0; j < 3; j++)
            {
                for (var r = 0; r < 255; r += 16)
                {
                    for (var g = 0; g < 255; g += 16)
                    {
                        for (var b = 0; b < 255; b += 16)
                        {
                            for (var i = 0; i < 3; i++)
                            {
                                imageUrls.Add($"{imageUrl}&bgcolor={r:x2}{g:x2}{b:x2}");
                            }
                        }
                    }
                }
            }
            return View(imageUrls);
        }


        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
