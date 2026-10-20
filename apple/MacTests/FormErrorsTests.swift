import Foundation
import Testing
import SpindleKit
@testable import Spindle

struct FormErrorsTests {
    @Test func validationErrorsGoNextToTheirFields() {
        let error = APIError(status: 400, title: "One or more validation errors occurred.", fieldErrors: [
            "email": ["Enter a valid email address."],
            "timeZone": ["Choose a time zone."],
        ])

        let errors = FormErrors(error, fields: ["email", "password"])

        #expect(errors["email"] == "Enter a valid email address.")
        #expect(errors["password"] == nil)
        // A field the form doesn't show goes on top.
        #expect(errors.form == "Choose a time zone.")
    }

    @Test func otherProblemsGoOnTop() {
        let error = APIError(status: 401, title: "Incorrect email or password.")

        let errors = FormErrors(error, fields: ["email", "password"])

        #expect(errors.fields.isEmpty)
        #expect(errors.form == "Incorrect email or password.")
    }

    @Test func connectionProblemsSaySo() {
        let errors = FormErrors(URLError(.notConnectedToInternet), fields: ["email"])

        #expect(errors.form == "Couldn't reach Spindle. Check your connection and try again.")
    }

    @Test(arguments: [
        ("", "Enter your email address."),
        ("ana", "Enter a valid email address."),
        ("ana@example", "Enter a valid email address."),
        ("ana @example.com", "Enter a valid email address."),
    ])
    func checksEmailAddresses(email: String, message: String) {
        #expect(Validation.email(email) == message)
    }

    @Test func acceptsEmailAddresses() {
        #expect(Validation.email("ana.kovac@example.com") == nil)
    }
}
