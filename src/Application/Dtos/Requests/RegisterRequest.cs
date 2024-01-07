using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using NPOI.SS.Formula.Functions;

namespace Application.Dtos.Requests
{
    public class RegisterRequest
    {

        [Required]
        public string DisplayName { get; set; }

        public string DisplayImage { get; set; } = default;
        [Required]
        public string Username { get; set; }
        [Required]
        public string Email { get; set; }
        [Required]
        public string Password { get; set; }
        [Required]
        public string[] Roles { get; set; }


    }
}